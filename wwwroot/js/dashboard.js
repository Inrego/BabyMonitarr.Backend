// SignalR connection
let connection;

// Video state
let videoConnections = {};        // { roomId: RTCPeerConnection }
let videoPendingCandidates = {};  // { roomId: [RTCIceCandidate] }
let videoTracks = {};             // { roomId: MediaStreamTrack }

// Audio state (per-room)
let audioConnections = {};        // { roomId: RTCPeerConnection }
let audioPendingCandidates = {};  // { roomId: [RTCIceCandidate] }
let audioElements = {};           // { roomId: HTMLAudioElement }

// State
let currentRooms = [];
let monitoringRooms = new Set();  // roomIds this client is actively monitoring
const DEFAULT_ICE_SERVERS = Object.freeze([{ urls: "stun:stun.l.google.com:19302" }]);
let webrtcIceServers = DEFAULT_ICE_SERVERS.map((server) => ({ ...server }));

// PWA state
const PWA_STORAGE_KEY = "babymonitarr.monitoringRoomIds";
const PWA_ALERT_COOLDOWN_MS = 30000; // 30 seconds, matching Flutter thresholdPauseDuration
const pwaLastAlertTime = {};         // { roomId: timestamp } — per-room cooldown tracking
let pwaWakeLock = null;              // Screen Wake Lock sentinel
let pwaInstallPrompt = null;         // Deferred beforeinstallprompt event

// Diagnostics state
const DIAG_PREFIX = "[BM-DIAG]";
const DIAG_STORAGE_KEY = "babymonitarr.webrtcDebug";
const DIAG_SESSION_ID = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
const DIAG_SESSION_START_MS = Date.now();
const DIAG_TRACK_TIMEOUT_MS = 15000;
const DIAG_PLAYING_TIMEOUT_MS = 10000;
const DIAG_DISCONNECT_GRACE_MS = 5000;
const DIAG_STREAM_STATE = new Map();   // key -> attempt state
const DIAG_STREAM_ATTEMPT_COUNTER = new Map(); // key -> number
const DIAG_STREAM_TIMERS = new Map();  // key -> timer handles
const DIAG_MEDIA_CLEANUP = new Map();  // key -> remove listeners function
const DIAG_VERBOSE = resolveDiagVerboseFlag();

function resolveDiagVerboseFlag() {
    let enabled = false;
    try {
        enabled = localStorage.getItem(DIAG_STORAGE_KEY) === "1";
    } catch {
        enabled = false;
    }

    let queryValue = null;
    try {
        const params = new URLSearchParams(window.location.search);
        queryValue = params.get("webrtcDebug");
    } catch {
        queryValue = null;
    }

    if (queryValue !== null) {
        const parsed = parseBooleanToggle(queryValue);
        if (parsed === true) {
            enabled = true;
            try { localStorage.setItem(DIAG_STORAGE_KEY, "1"); } catch { /* no-op */ }
            console.info(`${DIAG_PREFIX} verbose diagnostics enabled via query parameter`);
        } else if (parsed === false) {
            enabled = false;
            try { localStorage.removeItem(DIAG_STORAGE_KEY); } catch { /* no-op */ }
            console.info(`${DIAG_PREFIX} verbose diagnostics disabled via query parameter`);
        }
    }

    return enabled;
}

function parseBooleanToggle(value) {
    const normalized = String(value ?? "").trim().toLowerCase();
    if (["1", "true", "yes", "on"].includes(normalized)) return true;
    if (["0", "false", "no", "off"].includes(normalized)) return false;
    return null;
}

function normalizeIceServerEntry(entry) {
    if (!entry || typeof entry !== "object") {
        return null;
    }

    const urls = typeof entry.urls === "string" ? entry.urls.trim() : "";
    if (!urls) {
        return null;
    }

    const normalized = { urls };

    if (typeof entry.username === "string" && entry.username.trim()) {
        normalized.username = entry.username.trim();
    }

    if (typeof entry.credential === "string" && entry.credential.trim()) {
        normalized.credential = entry.credential.trim();
    }

    return normalized;
}

function summarizeIceServers(iceServers) {
    return iceServers.map((server) => ({
        urls: server.urls,
        hasUsername: !!server.username,
        hasCredential: !!server.credential
    }));
}

async function loadWebRtcConfig() {
    const fallback = DEFAULT_ICE_SERVERS.map((server) => ({ ...server }));

    try {
        const config = await invokeHubWithDiagnostics("GetWebRtcConfig", [], { area: "webrtc-config" });
        const configuredServers = Array.isArray(config?.iceServers)
            ? config.iceServers.map(normalizeIceServerEntry).filter((server) => !!server)
            : [];

        if (configuredServers.length > 0) {
            webrtcIceServers = configuredServers;
            diagInfo("webrtc.config.loaded", {
                source: "hub",
                serverCount: configuredServers.length,
                servers: summarizeIceServers(configuredServers)
            });
            return;
        }

        webrtcIceServers = fallback;
        diagWarn("webrtc.config.fallback", {
            reason: "missing-or-empty",
            serverCount: webrtcIceServers.length,
            servers: summarizeIceServers(webrtcIceServers)
        });
    } catch (error) {
        webrtcIceServers = fallback;
        diagWarn("webrtc.config.fallback", {
            reason: "load-failed",
            error: normalizeError(error),
            serverCount: webrtcIceServers.length,
            servers: summarizeIceServers(webrtcIceServers)
        });
    }
}

function diagInfo(event, context = {}) {
    if (!DIAG_VERBOSE) return;
    emitDiagLog("info", event, context);
}

function diagWarn(event, context = {}) {
    if (!DIAG_VERBOSE) return;
    emitDiagLog("warn", event, context);
}

function diagError(event, error, context = {}) {
    emitDiagLog("error", event, {
        ...context,
        error: normalizeError(error)
    });
}

function emitDiagLog(level, event, context = {}) {
    const payload = buildDiagContext(context);
    const serializedPayload = serializeDiagPayload(payload);
    const message = `${DIAG_PREFIX} ${event} ${serializedPayload}`;

    if (level === "warn") {
        console.warn(message);
        return;
    }

    if (level === "error") {
        console.error(message);
        return;
    }

    console.info(message);
}

function serializeDiagPayload(payload) {
    try {
        return JSON.stringify(payload, createDiagJsonReplacer());
    } catch (error) {
        return JSON.stringify({
            sessionId: DIAG_SESSION_ID,
            elapsedMs: Date.now() - DIAG_SESSION_START_MS,
            page: "dashboard",
            serializationError: normalizeError(error),
            payloadType: typeof payload,
            payloadPreview: String(payload)
        });
    }
}

function createDiagJsonReplacer() {
    const seen = new WeakSet();

    return function diagJsonReplacer(_key, value) {
        if (typeof value === "bigint") return value.toString();
        if (value instanceof Error) return normalizeError(value);
        if (value instanceof Date) return value.toISOString();
        if (value instanceof Set) return Array.from(value);
        if (value instanceof Map) return Object.fromEntries(value);

        if (typeof value === "function") {
            return `[Function ${value.name || "anonymous"}]`;
        }

        if (value && typeof value === "object") {
            if (seen.has(value)) {
                return "[Circular]";
            }
            seen.add(value);
        }

        return value;
    };
}

function buildDiagContext(context = {}) {
    return {
        sessionId: DIAG_SESSION_ID,
        elapsedMs: Date.now() - DIAG_SESSION_START_MS,
        page: "dashboard",
        ...context
    };
}

function normalizeError(error) {
    if (!error) return null;
    if (typeof error === "string") return { message: error };

    return {
        name: error.name ?? "Error",
        message: error.message ?? String(error),
        stack: error.stack ? String(error.stack).split("\n").slice(0, 3).join("\n") : null
    };
}

function getDisplayErrorMessage(error, fallbackMessage) {
    if (typeof error === "string" && error.trim()) {
        return error.trim();
    }

    if (error && typeof error.message === "string" && error.message.trim()) {
        return error.message.trim();
    }

    const normalized = normalizeError(error);
    if (normalized && typeof normalized.message === "string" && normalized.message.trim()) {
        return normalized.message.trim();
    }

    return fallbackMessage;
}

function logEnvironmentSnapshot() {
    diagInfo("environment.snapshot", {
        diagnosticsVerbose: DIAG_VERBOSE,
        location: window.location.href,
        origin: window.location.origin,
        secureContext: window.isSecureContext,
        visibilityState: document.visibilityState,
        userAgent: navigator.userAgent,
        language: navigator.language,
        timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
        signalRAvailable: typeof signalR !== "undefined",
        webRtcAvailable: typeof RTCPeerConnection !== "undefined"
    });
}

function getStreamKey(roomId, streamType) {
    return `${streamType}:${roomId}`;
}

function startStreamAttempt(roomId, streamType, context = {}) {
    const key = getStreamKey(roomId, streamType);
    const previousAttempt = DIAG_STREAM_ATTEMPT_COUNTER.get(key) ?? 0;
    const attempt = previousAttempt + 1;
    DIAG_STREAM_ATTEMPT_COUNTER.set(key, attempt);
    DIAG_STREAM_STATE.set(key, {
        roomId,
        streamType,
        attempt,
        startMs: Date.now(),
        context,
        stopRequested: false,
        flags: {},
        counters: {
            iceReceived: 0,
            iceQueued: 0,
            iceAdded: 0,
            iceSent: 0
        },
        milestones: {
            startRequestedAt: Date.now()
        }
    });

    clearAllStreamTimers(roomId, streamType);
    diagInfo("stream.attempt.start", { roomId, streamType, attempt, context });
}

function getStreamAttempt(roomId, streamType) {
    return DIAG_STREAM_STATE.get(getStreamKey(roomId, streamType)) ?? null;
}

function markStreamMilestone(roomId, streamType, name) {
    const attempt = getStreamAttempt(roomId, streamType);
    if (!attempt) return;
    if (!attempt.milestones[name]) {
        attempt.milestones[name] = Date.now();
    }
}

function incrementStreamCounter(roomId, streamType, name, amount = 1) {
    const attempt = getStreamAttempt(roomId, streamType);
    if (!attempt) return;
    attempt.counters[name] = (attempt.counters[name] ?? 0) + amount;
}

function setStreamFlag(roomId, streamType, name, value = true) {
    const attempt = getStreamAttempt(roomId, streamType);
    if (!attempt) return;
    attempt.flags[name] = value;
}

function setStreamStopRequested(roomId, streamType) {
    const attempt = getStreamAttempt(roomId, streamType);
    if (!attempt) return;
    attempt.stopRequested = true;
    markStreamMilestone(roomId, streamType, "stopRequestedAt");
}

function isStreamStopRequested(roomId, streamType) {
    const attempt = getStreamAttempt(roomId, streamType);
    return !!attempt?.stopRequested;
}

function finalizeStreamAttempt(roomId, streamType, status, extra = {}) {
    const key = getStreamKey(roomId, streamType);
    const attempt = DIAG_STREAM_STATE.get(key);
    if (!attempt) return;

    const endMs = Date.now();
    const milestones = {};
    Object.keys(attempt.milestones).forEach((milestone) => {
        milestones[milestone] = attempt.milestones[milestone] - attempt.startMs;
    });

    const summary = {
        roomId,
        streamType,
        attempt: attempt.attempt,
        status,
        durationMs: endMs - attempt.startMs,
        stopRequested: attempt.stopRequested,
        context: attempt.context,
        flags: attempt.flags,
        counters: attempt.counters,
        milestonesMs: milestones,
        ...extra
    };

    if (status === "failed") {
        diagWarn("stream.attempt.summary", summary);
    } else {
        diagInfo("stream.attempt.summary", summary);
    }

    clearAllStreamTimers(roomId, streamType);
    DIAG_STREAM_STATE.delete(key);
}

function setStreamTimer(roomId, streamType, timerName, timeoutMs, callback) {
    const key = getStreamKey(roomId, streamType);
    const timers = DIAG_STREAM_TIMERS.get(key) ?? {};
    if (timers[timerName]) {
        clearTimeout(timers[timerName]);
    }

    timers[timerName] = setTimeout(() => {
        timers[timerName] = null;
        callback();
    }, timeoutMs);

    DIAG_STREAM_TIMERS.set(key, timers);
}

function clearStreamTimer(roomId, streamType, timerName) {
    const key = getStreamKey(roomId, streamType);
    const timers = DIAG_STREAM_TIMERS.get(key);
    if (!timers || !timers[timerName]) return;
    clearTimeout(timers[timerName]);
    timers[timerName] = null;
}

function clearAllStreamTimers(roomId, streamType) {
    const key = getStreamKey(roomId, streamType);
    const timers = DIAG_STREAM_TIMERS.get(key);
    if (!timers) return;

    Object.keys(timers).forEach((timerName) => {
        if (timers[timerName]) {
            clearTimeout(timers[timerName]);
        }
    });
    DIAG_STREAM_TIMERS.delete(key);
}

function armTrackTimeout(roomId, streamType, pc) {
    setStreamTimer(roomId, streamType, "trackTimeout", DIAG_TRACK_TIMEOUT_MS, () => {
        const attempt = getStreamAttempt(roomId, streamType);
        if (!attempt || attempt.stopRequested || attempt.milestones.trackReceivedAt) return;

        setStreamFlag(roomId, streamType, "trackTimeout");
        diagWarn("webrtc.track.timeout", { roomId, streamType, timeoutMs: DIAG_TRACK_TIMEOUT_MS });
        void capturePeerStats(roomId, streamType, pc, "track-timeout");
    });
}

function armPlayingTimeout(roomId, streamType, pc) {
    setStreamTimer(roomId, streamType, "playingTimeout", DIAG_PLAYING_TIMEOUT_MS, () => {
        const attempt = getStreamAttempt(roomId, streamType);
        if (!attempt || attempt.stopRequested || attempt.milestones.firstPlayingAt) return;

        setStreamFlag(roomId, streamType, "playingTimeout");
        diagWarn("media.play.timeout", { roomId, streamType, timeoutMs: DIAG_PLAYING_TIMEOUT_MS });
        void capturePeerStats(roomId, streamType, pc, "playing-timeout");
    });
}

function armDisconnectedTimeout(roomId, streamType, pc) {
    setStreamTimer(roomId, streamType, "disconnectedTimeout", DIAG_DISCONNECT_GRACE_MS, () => {
        const attempt = getStreamAttempt(roomId, streamType);
        if (!attempt || attempt.stopRequested) return;

        if (!pc || (pc.connectionState !== "disconnected" && pc.connectionState !== "failed")) return;

        setStreamFlag(roomId, streamType, "disconnectedTimeout");
        diagWarn("webrtc.connection.disconnected.timeout", {
            roomId,
            streamType,
            timeoutMs: DIAG_DISCONNECT_GRACE_MS,
            connectionState: pc.connectionState
        });
        void capturePeerStats(roomId, streamType, pc, "disconnected-timeout");
    });
}

function attachMediaDiagnostics(element, roomId, streamType) {
    if (!element) return;

    const key = getStreamKey(roomId, streamType);
    detachMediaDiagnostics(roomId, streamType);

    const events = ["loadedmetadata", "canplay", "playing", "waiting", "stalled", "suspend", "pause", "ended", "error"];
    const listeners = [];

    events.forEach((eventName) => {
        const handler = () => {
            if (eventName === "playing") {
                markStreamMilestone(roomId, streamType, "firstPlayingAt");
                clearStreamTimer(roomId, streamType, "playingTimeout");
            }

            diagInfo("media.event", {
                roomId,
                streamType,
                event: eventName,
                mediaState: getMediaState(element),
                mediaError: summarizeMediaError(element.error)
            });

            if (eventName === "error") {
                setStreamFlag(roomId, streamType, "mediaElementError");
                void capturePeerStats(roomId, streamType, getPeerConnection(roomId, streamType), "media-error");
            }
        };

        element.addEventListener(eventName, handler);
        listeners.push({ eventName, handler });
    });

    DIAG_MEDIA_CLEANUP.set(key, () => {
        listeners.forEach((listener) => {
            element.removeEventListener(listener.eventName, listener.handler);
        });
    });

    diagInfo("media.attach", { roomId, streamType, mediaState: getMediaState(element) });
}

function detachMediaDiagnostics(roomId, streamType) {
    const key = getStreamKey(roomId, streamType);
    const cleanup = DIAG_MEDIA_CLEANUP.get(key);
    if (!cleanup) return;
    cleanup();
    DIAG_MEDIA_CLEANUP.delete(key);
}

function cleanupStreamDiagnostics(roomId, streamType) {
    clearAllStreamTimers(roomId, streamType);
    detachMediaDiagnostics(roomId, streamType);
}

function getMediaState(element) {
    if (!element) return null;

    const tracks = element.srcObject && typeof element.srcObject.getTracks === "function"
        ? element.srcObject.getTracks().map((track) => ({
            kind: track.kind,
            id: track.id,
            enabled: track.enabled,
            muted: track.muted,
            readyState: track.readyState
        }))
        : [];

    return {
        readyState: element.readyState,
        networkState: element.networkState,
        paused: element.paused,
        ended: element.ended,
        muted: element.muted,
        currentTime: element.currentTime,
        trackCount: tracks.length,
        tracks
    };
}

function summarizeMediaError(mediaError) {
    if (!mediaError) return null;
    const errorCodes = {
        1: "MEDIA_ERR_ABORTED",
        2: "MEDIA_ERR_NETWORK",
        3: "MEDIA_ERR_DECODE",
        4: "MEDIA_ERR_SRC_NOT_SUPPORTED"
    };
    return {
        code: mediaError.code,
        codeName: errorCodes[mediaError.code] ?? "UNKNOWN",
        message: mediaError.message ?? null
    };
}

function getPeerConnection(roomId, streamType) {
    return streamType === "audio" ? audioConnections[roomId] : videoConnections[roomId];
}

function parseIceCandidate(candidate) {
    if (!candidate || typeof candidate !== "string") return { present: false };

    const raw = candidate.startsWith("candidate:") ? candidate.slice(10) : candidate;
    const parts = raw.split(/\s+/);
    if (parts.length < 8) {
        return { present: true, parseable: false, length: candidate.length };
    }

    const typIndex = parts.indexOf("typ");
    const tcpTypeIndex = parts.indexOf("tcptype");

    return {
        present: true,
        parseable: true,
        protocol: parts[2],
        address: parts[4],
        port: parts[5],
        type: typIndex >= 0 ? parts[typIndex + 1] : "unknown",
        tcpType: tcpTypeIndex >= 0 ? parts[tcpTypeIndex + 1] : null
    };
}

function summarizeSdp(sdp) {
    if (!sdp || typeof sdp !== "string") return { present: false };
    const lines = sdp.split(/\r\n|\n/);
    return {
        present: true,
        length: sdp.length,
        lineCount: lines.length,
        hasAudio: sdp.includes("m=audio"),
        hasVideo: sdp.includes("m=video"),
        hasDataChannel: sdp.includes("m=application"),
        candidateLineCount: lines.filter((line) => line.startsWith("a=candidate:")).length
    };
}

function summarizeStatsReport(report) {
    const statsById = new Map();
    report.forEach((stat) => statsById.set(stat.id, stat));

    let selectedPairId = null;
    const inbound = [];
    const outbound = [];

    report.forEach((stat) => {
        if (stat.type === "transport" && stat.selectedCandidatePairId) {
            selectedPairId = stat.selectedCandidatePairId;
        }
        if (stat.type === "candidate-pair" && !selectedPairId && stat.selected) {
            selectedPairId = stat.id;
        }
        if (stat.type === "inbound-rtp" && !stat.isRemote) inbound.push(stat);
        if (stat.type === "outbound-rtp" && !stat.isRemote) outbound.push(stat);
    });

    const selectedPair = selectedPairId ? statsById.get(selectedPairId) : null;
    const localCandidate = selectedPair?.localCandidateId ? statsById.get(selectedPair.localCandidateId) : null;
    const remoteCandidate = selectedPair?.remoteCandidateId ? statsById.get(selectedPair.remoteCandidateId) : null;

    const inboundAudio = inbound.find((stat) => (stat.kind || stat.mediaType) === "audio");
    const inboundVideo = inbound.find((stat) => (stat.kind || stat.mediaType) === "video");
    const outboundAudio = outbound.find((stat) => (stat.kind || stat.mediaType) === "audio");
    const outboundVideo = outbound.find((stat) => (stat.kind || stat.mediaType) === "video");

    return {
        reportSize: report.size,
        selectedCandidatePair: selectedPair ? {
            state: selectedPair.state ?? null,
            currentRoundTripTime: selectedPair.currentRoundTripTime ?? null,
            availableIncomingBitrate: selectedPair.availableIncomingBitrate ?? null,
            availableOutgoingBitrate: selectedPair.availableOutgoingBitrate ?? null,
            local: localCandidate ? {
                candidateType: localCandidate.candidateType ?? null,
                protocol: localCandidate.protocol ?? null,
                address: localCandidate.address ?? localCandidate.ip ?? null,
                port: localCandidate.port ?? null
            } : null,
            remote: remoteCandidate ? {
                candidateType: remoteCandidate.candidateType ?? null,
                protocol: remoteCandidate.protocol ?? null,
                address: remoteCandidate.address ?? remoteCandidate.ip ?? null,
                port: remoteCandidate.port ?? null
            } : null
        } : null,
        inboundAudio: summarizeRtpStat(inboundAudio),
        inboundVideo: summarizeRtpStat(inboundVideo),
        outboundAudio: summarizeRtpStat(outboundAudio),
        outboundVideo: summarizeRtpStat(outboundVideo)
    };
}

function summarizeRtpStat(stat) {
    if (!stat) return null;
    return {
        kind: stat.kind || stat.mediaType || null,
        packetsReceived: stat.packetsReceived ?? null,
        packetsSent: stat.packetsSent ?? null,
        packetsLost: stat.packetsLost ?? null,
        jitter: stat.jitter ?? null,
        bytesReceived: stat.bytesReceived ?? null,
        bytesSent: stat.bytesSent ?? null,
        framesDecoded: stat.framesDecoded ?? null,
        framesPerSecond: stat.framesPerSecond ?? null
    };
}

async function capturePeerStats(roomId, streamType, pc, reason) {
    if (!DIAG_VERBOSE || !pc || typeof pc.getStats !== "function") return;

    const started = performance.now();
    try {
        const report = await pc.getStats();
        diagWarn("webrtc.stats.snapshot", {
            roomId,
            streamType,
            reason,
            durationMs: Math.round((performance.now() - started) * 100) / 100,
            connectionState: pc.connectionState,
            signalingState: pc.signalingState,
            summary: summarizeStatsReport(report)
        });
    } catch (error) {
        diagError("webrtc.stats.snapshot.failed", error, { roomId, streamType, reason });
    }
}

async function invokeHubWithDiagnostics(method, args, context = {}) {
    const started = performance.now();
    diagInfo("signalr.invoke.start", { method, argsCount: args.length, ...context });

    try {
        const result = await connection.invoke(method, ...args);
        const durationMs = Math.round((performance.now() - started) * 100) / 100;
        let resultSummary = null;
        if (method === "StartAudioStream" || method === "StartVideoStream") {
            resultSummary = summarizeSdp(result);
        } else if (Array.isArray(result)) {
            resultSummary = { type: "array", count: result.length };
        } else if (result && typeof result === "object") {
            resultSummary = { type: "object", keys: Object.keys(result).slice(0, 10) };
        } else {
            resultSummary = { type: typeof result };
        }

        diagInfo("signalr.invoke.success", { method, durationMs, result: resultSummary, ...context });
        return result;
    } catch (error) {
        const durationMs = Math.round((performance.now() - started) * 100) / 100;
        diagError("signalr.invoke.failed", error, { method, durationMs, ...context });
        throw error;
    }
}

document.addEventListener('DOMContentLoaded', function () {
    logEnvironmentSnapshot();

    try {
        initializeSignalRConnection();
    } catch (error) {
        diagError("signalr.initialize.failed", error);
        console.error("Error initializing SignalR connection:", error);
    }
});

function initializeSignalRConnection() {
    diagInfo("signalr.connection.build", { hubUrl: "/audioHub" });

    connection = new signalR.HubConnectionBuilder()
        .withUrl("/audioHub")
        .withAutomaticReconnect()
        .build();

    connection.onreconnecting((error) => {
        diagWarn("signalr.reconnecting", {
            state: connection.state,
            error: normalizeError(error)
        });
    });

    connection.onreconnected((connectionId) => {
        diagInfo("signalr.reconnected", {
            state: connection.state,
            connectionId: connectionId ?? null
        });
    });

    connection.onclose((error) => {
        diagWarn("signalr.closed", {
            state: connection.state,
            error: normalizeError(error)
        });
    });

    // Handle server ICE candidates (audio - per room)
    connection.on("ReceiveAudioIceCandidate", async (roomId, candidate, sdpMid, sdpMLineIndex) => {
        incrementStreamCounter(roomId, "audio", "iceReceived");

        if (!candidate || typeof candidate !== "string") {
            diagWarn("webrtc.ice.remote.invalid", {
                roomId,
                streamType: "audio",
                candidateType: typeof candidate
            });
            return;
        }

        const candidateStr = candidate.startsWith('candidate:') ? candidate : `candidate:${candidate}`;
        const iceCandidate = new RTCIceCandidate({
            candidate: candidateStr,
            sdpMid: sdpMid,
            sdpMLineIndex: sdpMLineIndex
        });

        diagInfo("webrtc.ice.remote.received", {
            roomId,
            streamType: "audio",
            candidate: parseIceCandidate(candidateStr),
            sdpMid: sdpMid ?? null,
            sdpMLineIndex: sdpMLineIndex ?? null
        });

        const pc = audioConnections[roomId];
        if (pc && pc.remoteDescription) {
            try {
                await pc.addIceCandidate(iceCandidate);
                incrementStreamCounter(roomId, "audio", "iceAdded");
                diagInfo("webrtc.ice.remote.added", { roomId, streamType: "audio" });
            } catch (err) {
                diagWarn("webrtc.ice.remote.add.failed", {
                    roomId,
                    streamType: "audio",
                    error: normalizeError(err)
                });
                console.warn(`Could not add audio ICE candidate for room ${roomId}:`, err.message);
            }
        } else {
            if (!audioPendingCandidates[roomId]) {
                audioPendingCandidates[roomId] = [];
            }
            audioPendingCandidates[roomId].push(iceCandidate);
            incrementStreamCounter(roomId, "audio", "iceQueued");
            diagInfo("webrtc.ice.remote.queued", {
                roomId,
                streamType: "audio",
                queueLength: audioPendingCandidates[roomId].length
            });
        }
    });

    // Handle server ICE candidates (video)
    connection.on("ReceiveVideoIceCandidate", async (roomId, candidate, sdpMid, sdpMLineIndex) => {
        incrementStreamCounter(roomId, "video", "iceReceived");

        if (!candidate || typeof candidate !== "string") {
            diagWarn("webrtc.ice.remote.invalid", {
                roomId,
                streamType: "video",
                candidateType: typeof candidate
            });
            return;
        }

        const candidateStr = candidate.startsWith('candidate:') ? candidate : `candidate:${candidate}`;
        const iceCandidate = new RTCIceCandidate({
            candidate: candidateStr,
            sdpMid: sdpMid,
            sdpMLineIndex: sdpMLineIndex
        });

        diagInfo("webrtc.ice.remote.received", {
            roomId,
            streamType: "video",
            candidate: parseIceCandidate(candidateStr),
            sdpMid: sdpMid ?? null,
            sdpMLineIndex: sdpMLineIndex ?? null
        });

        const pc = videoConnections[roomId];
        if (pc && pc.remoteDescription) {
            try {
                await pc.addIceCandidate(iceCandidate);
                incrementStreamCounter(roomId, "video", "iceAdded");
                diagInfo("webrtc.ice.remote.added", { roomId, streamType: "video" });
            } catch (err) {
                diagWarn("webrtc.ice.remote.add.failed", {
                    roomId,
                    streamType: "video",
                    error: normalizeError(err)
                });
                console.warn(`Could not add video ICE candidate for room ${roomId}:`, err.message);
            }
        } else {
            if (!videoPendingCandidates[roomId]) {
                videoPendingCandidates[roomId] = [];
            }
            videoPendingCandidates[roomId].push(iceCandidate);
            incrementStreamCounter(roomId, "video", "iceQueued");
            diagInfo("webrtc.ice.remote.queued", {
                roomId,
                streamType: "video",
                queueLength: videoPendingCandidates[roomId].length
            });
        }
    });

    // Handle room updates
    connection.on("RoomsUpdated", async () => {
        diagInfo("signalr.event.roomsUpdated");
        await loadRooms();
    });

    connection.start()
        .then(async () => {
            console.log("Dashboard SignalR Connected");
            diagInfo("signalr.connected", {
                state: connection.state,
                connectionId: connection.connectionId ?? null
            });
            await loadWebRtcConfig();
            await loadRooms();
        })
        .catch(err => {
            diagError("signalr.start.failed", err);
            console.error(err);
            setTimeout(initializeSignalRConnection, 5000);
        });
}

async function loadRooms() {
    try {
        currentRooms = await invokeHubWithDiagnostics("GetRooms", [], { area: "dashboard" });
        const currentRoomIds = new Set(currentRooms.map(r => r.id));
        diagInfo("rooms.loaded", { roomCount: currentRooms.length, roomIds: currentRooms.map(r => r.id) });

        // Stop streams for rooms that were removed from config
        for (const roomId of monitoringRooms) {
            if (!currentRoomIds.has(roomId)) {
                diagWarn("rooms.removed.while.monitoring", { roomId });
                await stopMonitoring(roomId, true);
            }
        }

        renderDashboard();
        pwaAutoResumeMonitoring();
    } catch (err) {
        diagError("rooms.load.failed", err);
        console.error("Error loading rooms:", err);
    }
}

// ===== Dashboard Rendering =====
function renderDashboard() {
    const grid = document.getElementById('dashboardGrid');
    const empty = document.getElementById('dashboardEmpty');
    if (!grid || !empty) return;

    diagInfo("dashboard.render", {
        roomCount: currentRooms.length,
        monitoringRoomCount: monitoringRooms.size
    });

    if (currentRooms.length === 0) {
        grid.style.display = 'none';
        empty.style.display = 'block';
        return;
    }

    grid.style.display = '';
    empty.style.display = 'none';

    grid.innerHTML = currentRooms.map(room => {
        if (monitoringRooms.has(room.id)) {
            return renderMonitoringCard(room);
        } else {
            return renderInactiveCard(room);
        }
    }).join('');

    reattachVideoStreams();
}

function updateRoomCard(roomId) {
    const room = currentRooms.find(r => r.id === roomId);
    if (!room) return;
    const existingCard = document.querySelector(`.dash-card[data-room-id="${roomId}"]`);
    if (!existingCard) return;
    const html = monitoringRooms.has(roomId) ? renderMonitoringCard(room) : renderInactiveCard(room);
    const temp = document.createElement('div');
    temp.innerHTML = html;
    existingCard.replaceWith(temp.firstElementChild);
}

function reattachVideoStreams() {
    for (const roomId of Object.keys(videoConnections)) {
        const track = videoTracks[roomId];
        const videoEl = document.getElementById(`video-${roomId}`);
        if (!videoEl || !track || track.readyState !== 'live') continue;

        attachMediaDiagnostics(videoEl, roomId, "video");
        videoEl.srcObject = new MediaStream([track]);
        videoEl.style.display = '';
        markStreamMilestone(roomId, "video", "firstPlayAttemptAt");
        videoEl.play()
            .then(() => {
                markStreamMilestone(roomId, "video", "firstPlayResolvedAt");
                diagInfo("media.play.resolved", {
                    roomId,
                    streamType: "video",
                    reason: "reattach"
                });
            })
            .catch(e => {
                setStreamFlag(roomId, "video", "playRejected");
                diagError("media.play.failed", e, {
                    roomId,
                    streamType: "video",
                    reason: "reattach",
                    mediaState: getMediaState(videoEl)
                });
                void capturePeerStats(roomId, "video", videoConnections[roomId], "play-failed-reattach");
                console.error(`Error replaying video for room ${roomId}:`, e);
            });
        const iconEl = document.getElementById(`iconPlaceholder-${roomId}`);
        if (iconEl) iconEl.style.display = 'none';
        const loadingEl = document.getElementById(`videoLoading-${roomId}`);
        if (loadingEl) loadingEl.style.display = 'none';
    }
}

function renderInactiveCard(room) {
    return `
        <div class="dash-card" data-room-id="${room.id}">
            <div class="dash-card-header">
                <div class="dash-card-header-icon">
                    <i class="fas fa-${escapeHtml(room.icon || 'baby')}"></i>
                </div>
                <span class="dash-card-header-name">${escapeHtml(room.name)}</span>
                <div class="dash-card-header-badges">
                    <span class="status-badge inactive">
                        <span class="status-dot"></span>
                        Inactive
                    </span>
                </div>
            </div>
            <div class="dash-card-inactive-body">
                <div class="dash-card-inactive-icon">
                    <i class="fas fa-${escapeHtml(room.icon || 'baby')}"></i>
                </div>
                <div class="dash-card-inactive-message">Monitor is not active</div>
                <button class="btn-start-monitoring" onclick="startMonitoring(${room.id})">
                    <i class="fas fa-play"></i> Start Monitoring
                </button>
                <div class="dash-card-inactive-links">
                    <a href="/Home/Index?editRoom=${room.id}"><i class="fas fa-cog"></i> Settings</a>
                </div>
            </div>
        </div>
    `;
}

function renderMonitoringCard(room) {
    const hasVideo = room.enableVideoStream && (room.cameraStreamUrl || room.nestDeviceId);
    const hasAudio = room.enableAudioStream && (room.cameraStreamUrl || room.nestDeviceId);
    const isMuted = !audioElements[room.id] || audioElements[room.id].muted;

    return `
        <div class="dash-card" data-room-id="${room.id}">
            <div class="dash-card-header">
                <div class="dash-card-header-icon">
                    <i class="fas fa-${escapeHtml(room.icon || 'baby')}"></i>
                </div>
                <span class="dash-card-header-name">${escapeHtml(room.name)}</span>
                <div class="dash-card-header-badges">
                    <span class="status-badge monitoring">
                        <span class="status-dot"></span>
                        Monitoring
                    </span>
                    <span class="dash-card-live-badge">LIVE</span>
                </div>
            </div>
            <div class="dash-card-preview">
                <video id="video-${room.id}" class="dash-card-video" autoplay muted playsinline
                       style="display: none;"></video>
                <div id="iconPlaceholder-${room.id}" class="dash-card-icon-placeholder">
                    <i class="fas fa-${escapeHtml(room.icon || 'baby')}"></i>
                </div>
                <div id="videoLoading-${room.id}" class="dash-card-video-loading" style="display: none;">
                    <i class="fas fa-spinner fa-spin"></i>
                </div>
            </div>
            <div class="dash-card-monitoring-info">
                <span id="dbLevel-${room.id}" class="dash-card-db-level">--.- dB</span>
                <div class="dash-card-meter-bar">
                    <div id="meter-${room.id}" class="dash-card-meter-fill"></div>
                </div>
            </div>
            <div class="dash-card-actions">
                <button class="btn-dash-action btn-dash-mute ${isMuted ? 'muted' : ''}" onclick="toggleMute(${room.id})" ${!hasAudio ? 'disabled' : ''}>
                    <i class="fas fa-${isMuted ? 'volume-mute' : 'volume-up'}"></i>
                    ${isMuted ? 'Muted' : 'Sound On'}
                </button>
                <button class="btn-dash-action btn-dash-stop" onclick="stopMonitoring(${room.id})">
                    <i class="fas fa-stop"></i>
                    Stop
                </button>
            </div>
        </div>
    `;
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// ===== Start / Stop Monitoring =====
async function startMonitoring(roomId) {
    if (monitoringRooms.has(roomId)) {
        diagInfo("monitoring.start.ignored.alreadyMonitoring", { roomId });
        return;
    }
    const room = currentRooms.find(r => r.id === roomId);
    if (!room) {
        diagWarn("monitoring.start.failed.roomMissing", { roomId });
        return;
    }

    monitoringRooms.add(roomId);
    pwaRequestNotificationPermission();
    pwaSaveMonitoringState();
    pwaAcquireWakeLock();
    updateRoomCard(roomId);

    const hasAudio = room.enableAudioStream && (room.cameraStreamUrl || room.nestDeviceId);
    const hasVideo = room.enableVideoStream && (room.cameraStreamUrl || room.nestDeviceId);

    diagInfo("monitoring.start", {
        roomId,
        roomName: room.name,
        streamSourceType: room.streamSourceType || "rtsp",
        hasAudio,
        hasVideo
    });

    // Start streams (audio unmuted by default — noise meter data arrives via data channel)
    if (hasAudio) {
        await startAudioStream(roomId);
        updateMuteButton(roomId);
    }
    if (hasVideo) {
        await startVideoStream(roomId);
    }

    pwaUpdateMediaSession();
}

async function stopMonitoring(roomId, skipRender) {
    diagInfo("monitoring.stop", { roomId, skipRender: !!skipRender });

    // Stop both streams
    if (audioConnections[roomId]) {
        await stopAudioStream(roomId);
    }
    if (videoConnections[roomId]) {
        await stopVideoStream(roomId);
    }

    monitoringRooms.delete(roomId);
    pwaSaveMonitoringState();
    pwaUpdateMediaSession();

    if (monitoringRooms.size === 0) {
        pwaReleaseWakeLock();
    }

    if (!skipRender) {
        updateRoomCard(roomId);
    }
}

// ===== Video Stream Management =====
function canStartVideoForRoom(room) {
    if (!room || !room.enableVideoStream) {
        return false;
    }

    if (room.streamSourceType === 'google_nest') {
        return !!room.nestDeviceId;
    }

    return !!room.cameraStreamUrl;
}

async function startVideoStream(roomId) {
    const streamType = "video";
    const room = currentRooms.find(r => r.id === roomId);
    startStreamAttempt(roomId, streamType, {
        sourceType: room?.streamSourceType || "rtsp"
    });

    try {
        // Show loading indicator
        const loadingEl = document.getElementById(`videoLoading-${roomId}`);
        if (loadingEl) loadingEl.style.display = '';

        const videoEl = document.getElementById(`video-${roomId}`);
        attachMediaDiagnostics(videoEl, roomId, streamType);

        const offerSdp = await invokeHubWithDiagnostics("StartVideoStream", [roomId], {
            roomId,
            streamType
        });
        markStreamMilestone(roomId, streamType, "offerReceivedAt");

        if (!offerSdp) {
            setStreamFlag(roomId, streamType, "missingOffer");
            diagWarn("webrtc.offer.missing", { roomId, streamType });
            console.error(`No SDP offer received for video room ${roomId}`);
            if (loadingEl) loadingEl.style.display = 'none';
            finalizeStreamAttempt(roomId, streamType, "failed", { reason: "missing-offer" });
            showMessage("No SDP offer received for video stream", true);
            return;
        }

        diagInfo("webrtc.offer.received", {
            roomId,
            streamType,
            sdp: summarizeSdp(offerSdp)
        });

        const configuration = {
            iceServers: webrtcIceServers
        };

        const pc = new RTCPeerConnection(configuration);
        videoConnections[roomId] = pc;

        // Handle ICE candidates
        pc.onicecandidate = async (event) => {
            if (event.candidate) {
                incrementStreamCounter(roomId, streamType, "iceSent");
                diagInfo("webrtc.ice.local.generated", {
                    roomId,
                    streamType,
                    candidate: parseIceCandidate(event.candidate.candidate),
                    sdpMid: event.candidate.sdpMid ?? null,
                    sdpMLineIndex: event.candidate.sdpMLineIndex ?? null
                });

                try {
                    await invokeHubWithDiagnostics("AddVideoIceCandidate", [
                        roomId,
                        event.candidate.candidate,
                        event.candidate.sdpMid,
                        event.candidate.sdpMLineIndex
                    ], { roomId, streamType });
                } catch (err) {
                    diagError("webrtc.ice.local.send.failed", err, { roomId, streamType });
                    console.error(`Error sending video ICE candidate for room ${roomId}:`, err);
                }
            }
        };

        pc.onsignalingstatechange = () => {
            diagInfo("webrtc.signaling.state", { roomId, streamType, state: pc.signalingState });
        };

        pc.onicegatheringstatechange = () => {
            diagInfo("webrtc.iceGathering.state", { roomId, streamType, state: pc.iceGatheringState });
        };

        pc.oniceconnectionstatechange = () => {
            diagInfo("webrtc.iceConnection.state", { roomId, streamType, state: pc.iceConnectionState });
        };

        pc.onicecandidateerror = (event) => {
            diagWarn("webrtc.ice.candidate.error", {
                roomId,
                streamType,
                errorCode: event.errorCode,
                errorText: event.errorText,
                hostCandidate: event.hostCandidate ?? null,
                url: event.url ?? null
            });
        };

        // Handle video track arrival
        pc.ontrack = (event) => {
            markStreamMilestone(roomId, streamType, "trackReceivedAt");
            clearStreamTimer(roomId, streamType, "trackTimeout");

            diagInfo("webrtc.track.received", {
                roomId,
                streamType,
                kind: event.track?.kind ?? null,
                trackId: event.track?.id ?? null,
                streamCount: event.streams?.length ?? 0
            });
            console.log(`Video track received for room ${roomId}`, event.track.kind);
            if (event.track.kind === 'video') {
                videoTracks[roomId] = event.track;

                const videoEl = document.getElementById(`video-${roomId}`);
                const iconEl = document.getElementById(`iconPlaceholder-${roomId}`);
                const loadingIndicator = document.getElementById(`videoLoading-${roomId}`);

                if (videoEl) {
                    attachMediaDiagnostics(videoEl, roomId, streamType);
                    videoEl.srcObject = event.streams[0] || new MediaStream([event.track]);
                    videoEl.style.display = '';
                    markStreamMilestone(roomId, streamType, "firstPlayAttemptAt");
                    armPlayingTimeout(roomId, streamType, pc);
                    videoEl.play()
                        .then(() => {
                            markStreamMilestone(roomId, streamType, "firstPlayResolvedAt");
                            diagInfo("media.play.resolved", {
                                roomId,
                                streamType,
                                reason: "track-arrival"
                            });
                        })
                        .catch(e => {
                            setStreamFlag(roomId, streamType, "playRejected");
                            diagError("media.play.failed", e, {
                                roomId,
                                streamType,
                                reason: "track-arrival",
                                mediaState: getMediaState(videoEl)
                            });
                            void capturePeerStats(roomId, streamType, pc, "play-failed");
                            console.error(`Error playing video for room ${roomId}:`, e);
                        });
                }
                if (iconEl) iconEl.style.display = 'none';
                if (loadingIndicator) loadingIndicator.style.display = 'none';
            }
        };

        // Handle connection state changes
        pc.onconnectionstatechange = () => {
            const state = pc.connectionState;
            markStreamMilestone(roomId, streamType, `connectionState_${state}At`);
            diagInfo("webrtc.connection.state", { roomId, streamType, state });
            console.log(`Video connection state for room ${roomId}:`, state);

            if (state === 'connected') {
                clearStreamTimer(roomId, streamType, "disconnectedTimeout");
            }

            if (state === 'disconnected') {
                armDisconnectedTimeout(roomId, streamType, pc);
            }

            if (state === 'failed' && !isStreamStopRequested(roomId, streamType)) {
                setStreamFlag(roomId, streamType, "connectionFailed");
                void capturePeerStats(roomId, streamType, pc, "connection-failed");
                finalizeStreamAttempt(roomId, streamType, "failed", { reason: "connection-failed" });
            }

            if (state === 'disconnected' || state === 'failed' || state === 'closed') {
                onVideoDisconnected(roomId);
            }
        };

        // Set remote description (the offer from server)
        await pc.setRemoteDescription({ type: 'offer', sdp: offerSdp });
        markStreamMilestone(roomId, streamType, "remoteDescriptionSetAt");
        diagInfo("webrtc.remoteDescription.set", {
            roomId,
            streamType,
            signalingState: pc.signalingState
        });

        // Create and send answer
        const answer = await pc.createAnswer();
        markStreamMilestone(roomId, streamType, "answerCreatedAt");
        await pc.setLocalDescription(answer);
        markStreamMilestone(roomId, streamType, "localDescriptionSetAt");
        await invokeHubWithDiagnostics("SetVideoRemoteDescription", [roomId, answer.type, answer.sdp], {
            roomId,
            streamType
        });
        markStreamMilestone(roomId, streamType, "answerSentAt");

        // Process queued ICE candidates
        if (videoPendingCandidates[roomId] && videoPendingCandidates[roomId].length > 0) {
            diagInfo("webrtc.ice.remote.queue.processing", {
                roomId,
                streamType,
                queuedCount: videoPendingCandidates[roomId].length
            });
            for (const candidate of videoPendingCandidates[roomId]) {
                try {
                    await pc.addIceCandidate(candidate);
                    incrementStreamCounter(roomId, streamType, "iceAdded");
                } catch (err) {
                    diagWarn("webrtc.ice.remote.queued.add.failed", {
                        roomId,
                        streamType,
                        error: normalizeError(err)
                    });
                    console.warn(`Could not add queued video ICE candidate for room ${roomId}:`, err.message);
                }
            }
            videoPendingCandidates[roomId] = [];
        }

        armTrackTimeout(roomId, streamType, pc);

    } catch (error) {
        setStreamFlag(roomId, streamType, "startException");
        diagError("webrtc.stream.start.failed", error, { roomId, streamType });
        void capturePeerStats(roomId, streamType, videoConnections[roomId], "start-exception");
        finalizeStreamAttempt(roomId, streamType, "failed", { reason: "start-exception" });
        console.error(`Error starting video stream for room ${roomId}:`, error);
        showMessage(getDisplayErrorMessage(error, "Error starting video stream"), true);
        const loadingEl = document.getElementById(`videoLoading-${roomId}`);
        if (loadingEl) loadingEl.style.display = 'none';
    }
}

async function stopVideoStream(roomId) {
    const streamType = "video";
    setStreamStopRequested(roomId, streamType);

    const pc = videoConnections[roomId];
    if (pc) {
        await capturePeerStats(roomId, streamType, pc, "stop-requested");

        try {
            await invokeHubWithDiagnostics("StopVideoStream", [roomId], { roomId, streamType });
        } catch (err) {
            diagError("webrtc.stream.stop.failed", err, { roomId, streamType });
            console.error(`Error stopping video stream for room ${roomId}:`, err);
        }
        pc.close();
        delete videoConnections[roomId];
    }
    delete videoPendingCandidates[roomId];
    delete videoTracks[roomId];
    onVideoDisconnected(roomId);

    cleanupStreamDiagnostics(roomId, streamType);
    finalizeStreamAttempt(roomId, streamType, "stopped", { reason: "stop-monitoring" });
}

function onVideoDisconnected(roomId) {
    const videoEl = document.getElementById(`video-${roomId}`);
    const iconEl = document.getElementById(`iconPlaceholder-${roomId}`);
    const loadingEl = document.getElementById(`videoLoading-${roomId}`);

    if (videoEl) {
        videoEl.srcObject = null;
        videoEl.style.display = 'none';
    }
    if (iconEl) iconEl.style.display = '';
    if (loadingEl) loadingEl.style.display = 'none';
}

// ===== Audio Stream Management (per-room) =====
async function startAudioStream(roomId) {
    const streamType = "audio";
    const room = currentRooms.find(r => r.id === roomId);
    startStreamAttempt(roomId, streamType, {
        sourceType: room?.streamSourceType || "rtsp"
    });

    try {
        const offerSdp = await invokeHubWithDiagnostics("StartAudioStream", [roomId], {
            roomId,
            streamType
        });
        markStreamMilestone(roomId, streamType, "offerReceivedAt");

        if (!offerSdp) {
            setStreamFlag(roomId, streamType, "missingOffer");
            diagWarn("webrtc.offer.missing", { roomId, streamType });
            console.error(`No SDP offer received for audio room ${roomId}`);
            finalizeStreamAttempt(roomId, streamType, "failed", { reason: "missing-offer" });
            return;
        }

        diagInfo("webrtc.offer.received", {
            roomId,
            streamType,
            sdp: summarizeSdp(offerSdp)
        });

        const configuration = {
            iceServers: webrtcIceServers
        };

        const pc = new RTCPeerConnection(configuration);
        audioConnections[roomId] = pc;

        // Create audio element for this room — unmuted by default (sound on)
        const audioEl = document.createElement('audio');
        audioEl.setAttribute('autoplay', 'true');
        audioEl.setAttribute('playsinline', 'true');
        audioEl.style.display = 'none';
        audioEl.muted = false;
        document.body.appendChild(audioEl);
        audioElements[roomId] = audioEl;
        attachMediaDiagnostics(audioEl, roomId, streamType);

        // Handle ICE candidates
        pc.onicecandidate = async (event) => {
            if (event.candidate) {
                incrementStreamCounter(roomId, streamType, "iceSent");
                diagInfo("webrtc.ice.local.generated", {
                    roomId,
                    streamType,
                    candidate: parseIceCandidate(event.candidate.candidate),
                    sdpMid: event.candidate.sdpMid ?? null,
                    sdpMLineIndex: event.candidate.sdpMLineIndex ?? null
                });

                try {
                    await invokeHubWithDiagnostics("AddAudioIceCandidate", [
                        roomId,
                        event.candidate.candidate,
                        event.candidate.sdpMid,
                        event.candidate.sdpMLineIndex
                    ], { roomId, streamType });
                } catch (err) {
                    diagError("webrtc.ice.local.send.failed", err, { roomId, streamType });
                    console.error(`Error sending audio ICE candidate for room ${roomId}:`, err);
                }
            }
        };

        pc.onsignalingstatechange = () => {
            diagInfo("webrtc.signaling.state", { roomId, streamType, state: pc.signalingState });
        };

        pc.onicegatheringstatechange = () => {
            diagInfo("webrtc.iceGathering.state", { roomId, streamType, state: pc.iceGatheringState });
        };

        pc.oniceconnectionstatechange = () => {
            diagInfo("webrtc.iceConnection.state", { roomId, streamType, state: pc.iceConnectionState });
        };

        pc.onicecandidateerror = (event) => {
            diagWarn("webrtc.ice.candidate.error", {
                roomId,
                streamType,
                errorCode: event.errorCode,
                errorText: event.errorText,
                hostCandidate: event.hostCandidate ?? null,
                url: event.url ?? null
            });
        };

        // Handle data channel (audio levels, sound alerts)
        pc.ondatachannel = (event) => {
            const dataChannel = event.channel;
            markStreamMilestone(roomId, streamType, "dataChannelReceivedAt");
            diagInfo("webrtc.dataChannel.received", {
                roomId,
                streamType,
                label: dataChannel.label
            });

            dataChannel.onopen = () => {
                markStreamMilestone(roomId, streamType, "dataChannelOpenedAt");
                diagInfo("webrtc.dataChannel.open", {
                    roomId,
                    streamType,
                    label: dataChannel.label
                });
            };

            dataChannel.onmessage = (event) => {
                try {
                    const message = JSON.parse(event.data);
                    if (message.type === 'audioLevel') {
                        updateCardMeter(roomId, message.level);
                    } else if (message.type === 'soundAlert') {
                        diagInfo("webrtc.dataChannel.soundAlert", {
                            roomId,
                            streamType,
                            level: message.level,
                            threshold: message.threshold
                        });
                        console.log(`Sound alert for room ${roomId}: ${message.level.toFixed(1)} dB (threshold: ${message.threshold.toFixed(1)} dB)`);
                        handleSoundAlert(roomId, message.level, message.threshold);
                    } else {
                        diagInfo("webrtc.dataChannel.unknownMessage", {
                            roomId,
                            streamType,
                            messageType: message.type || "unknown"
                        });
                    }
                } catch (err) {
                    diagError("webrtc.dataChannel.parse.failed", err, { roomId, streamType });
                    console.error("Error parsing data channel message:", err);
                }
            };

            dataChannel.onclose = () => {
                diagInfo("webrtc.dataChannel.close", {
                    roomId,
                    streamType,
                    label: dataChannel.label
                });
            };

            dataChannel.onerror = (error) => {
                diagWarn("webrtc.dataChannel.error", {
                    roomId,
                    streamType,
                    label: dataChannel.label,
                    error: normalizeError(error)
                });
            };
        };

        // Handle audio track arrival
        pc.ontrack = (event) => {
            markStreamMilestone(roomId, streamType, "trackReceivedAt");
            clearStreamTimer(roomId, streamType, "trackTimeout");

            diagInfo("webrtc.track.received", {
                roomId,
                streamType,
                kind: event.track?.kind ?? null,
                trackId: event.track?.id ?? null,
                streamCount: event.streams?.length ?? 0
            });

            if (event.track.kind === 'audio' && audioEl) {
                audioEl.srcObject = event.streams[0] || new MediaStream([event.track]);
                markStreamMilestone(roomId, streamType, "firstPlayAttemptAt");
                armPlayingTimeout(roomId, streamType, pc);
                audioEl.play()
                    .then(() => {
                        markStreamMilestone(roomId, streamType, "firstPlayResolvedAt");
                        diagInfo("media.play.resolved", { roomId, streamType, reason: "track-arrival" });
                    })
                    .catch(e => {
                        // Autoplay policy may block unmuted playback — fall back to muted
                        setStreamFlag(roomId, streamType, "autoplayBlocked");
                        diagWarn("media.play.blocked.autoplayFallback", {
                            roomId,
                            streamType,
                            error: normalizeError(e),
                            mediaState: getMediaState(audioEl)
                        });

                        console.warn(`Autoplay blocked for room ${roomId}, falling back to muted:`, e.message);
                        audioEl.muted = true;
                        markStreamMilestone(roomId, streamType, "fallbackPlayAttemptAt");
                        audioEl.play()
                            .then(() => {
                                markStreamMilestone(roomId, streamType, "fallbackPlayResolvedAt");
                                diagInfo("media.play.fallback.resolved", { roomId, streamType });
                            })
                            .catch(e2 => {
                                setStreamFlag(roomId, streamType, "playRejected");
                                diagError("media.play.failed", e2, {
                                    roomId,
                                    streamType,
                                    reason: "autoplay-fallback",
                                    mediaState: getMediaState(audioEl)
                                });
                                void capturePeerStats(roomId, streamType, pc, "play-failed");
                                console.error(`Error playing audio for room ${roomId}:`, e2);
                            });
                        updateMuteButton(roomId);
                    });
            }
        };

        // Handle connection state changes
        pc.onconnectionstatechange = () => {
            const state = pc.connectionState;
            markStreamMilestone(roomId, streamType, `connectionState_${state}At`);
            diagInfo("webrtc.connection.state", { roomId, streamType, state });
            console.log(`Audio connection state for room ${roomId}:`, state);

            if (state === 'connected') {
                clearStreamTimer(roomId, streamType, "disconnectedTimeout");
            }

            if (state === 'disconnected') {
                armDisconnectedTimeout(roomId, streamType, pc);
            }

            if (state === 'failed' && !isStreamStopRequested(roomId, streamType)) {
                setStreamFlag(roomId, streamType, "connectionFailed");
                void capturePeerStats(roomId, streamType, pc, "connection-failed");
                finalizeStreamAttempt(roomId, streamType, "failed", { reason: "connection-failed" });
            }

            if ((state === 'disconnected' || state === 'failed') && !isStreamStopRequested(roomId, streamType)) {
                const room = currentRooms.find(r => r.id === roomId);
                const roomName = room ? room.name : `Room ${roomId}`;
                pwaShowNotification(
                    `Connection lost \u2014 ${roomName}`,
                    'Attempting to reconnect...',
                    `disconnect-${roomId}`
                );
            }

            if (state === 'disconnected' || state === 'failed' || state === 'closed') {
                onAudioDisconnected(roomId);
            }
        };

        // Set remote description (the offer from server)
        await pc.setRemoteDescription({ type: 'offer', sdp: offerSdp });
        markStreamMilestone(roomId, streamType, "remoteDescriptionSetAt");
        diagInfo("webrtc.remoteDescription.set", {
            roomId,
            streamType,
            signalingState: pc.signalingState
        });

        // Create and send answer
        const answer = await pc.createAnswer();
        markStreamMilestone(roomId, streamType, "answerCreatedAt");
        await pc.setLocalDescription(answer);
        markStreamMilestone(roomId, streamType, "localDescriptionSetAt");
        await invokeHubWithDiagnostics("SetAudioRemoteDescription", [roomId, answer.type, answer.sdp], {
            roomId,
            streamType
        });
        markStreamMilestone(roomId, streamType, "answerSentAt");

        // Process queued ICE candidates
        if (audioPendingCandidates[roomId] && audioPendingCandidates[roomId].length > 0) {
            diagInfo("webrtc.ice.remote.queue.processing", {
                roomId,
                streamType,
                queuedCount: audioPendingCandidates[roomId].length
            });
            for (const candidate of audioPendingCandidates[roomId]) {
                try {
                    await pc.addIceCandidate(candidate);
                    incrementStreamCounter(roomId, streamType, "iceAdded");
                } catch (err) {
                    diagWarn("webrtc.ice.remote.queued.add.failed", {
                        roomId,
                        streamType,
                        error: normalizeError(err)
                    });
                    console.warn(`Could not add queued audio ICE candidate for room ${roomId}:`, err.message);
                }
            }
            audioPendingCandidates[roomId] = [];
        }

        armTrackTimeout(roomId, streamType, pc);

    } catch (error) {
        setStreamFlag(roomId, streamType, "startException");
        diagError("webrtc.stream.start.failed", error, { roomId, streamType });
        void capturePeerStats(roomId, streamType, audioConnections[roomId], "start-exception");
        finalizeStreamAttempt(roomId, streamType, "failed", { reason: "start-exception" });
        console.error(`Error starting audio stream for room ${roomId}:`, error);
        showMessage("Error starting audio stream", true);
    }
}

async function stopAudioStream(roomId) {
    const streamType = "audio";
    setStreamStopRequested(roomId, streamType);

    const pc = audioConnections[roomId];
    if (pc) {
        await capturePeerStats(roomId, streamType, pc, "stop-requested");

        try {
            await invokeHubWithDiagnostics("StopAudioStream", [roomId], { roomId, streamType });
        } catch (err) {
            diagError("webrtc.stream.stop.failed", err, { roomId, streamType });
            console.error(`Error stopping audio stream for room ${roomId}:`, err);
        }
        pc.close();
        delete audioConnections[roomId];
    }
    delete audioPendingCandidates[roomId];

    // Clean up audio element
    if (audioElements[roomId]) {
        audioElements[roomId].srcObject = null;
        audioElements[roomId].remove();
        delete audioElements[roomId];
    }

    onAudioDisconnected(roomId);

    cleanupStreamDiagnostics(roomId, streamType);
    finalizeStreamAttempt(roomId, streamType, "stopped", { reason: "stop-monitoring" });
}

function onAudioDisconnected(roomId) {
    // Reset meter
    const meter = document.getElementById(`meter-${roomId}`);
    if (meter) meter.style.width = '0%';
    const dbLabel = document.getElementById(`dbLevel-${roomId}`);
    if (dbLabel) dbLabel.textContent = '--.- dB';
}

// ===== Mute Toggle (per-room, surgical DOM update) =====
function toggleMute(roomId) {
    const audioEl = audioElements[roomId];
    if (!audioEl) return;

    audioEl.muted = !audioEl.muted;
    diagInfo("audio.mute.toggled", { roomId, muted: audioEl.muted });
    updateMuteButton(roomId);
}

function updateMuteButton(roomId) {
    const card = document.querySelector(`.dash-card[data-room-id="${roomId}"]`);
    if (!card) return;

    const audioEl = audioElements[roomId];
    if (!audioEl) return;

    const isMuted = audioEl.muted;
    const muteBtn = card.querySelector('.btn-dash-mute');
    if (muteBtn) {
        muteBtn.className = `btn-dash-action btn-dash-mute ${isMuted ? 'muted' : ''}`;
        muteBtn.innerHTML = `<i class="fas fa-${isMuted ? 'volume-mute' : 'volume-up'}"></i> ${isMuted ? 'Muted' : 'Sound On'}`;
    }
}

// ===== Card Meter Updates =====
function updateCardMeter(roomId, level) {
    const meter = document.getElementById(`meter-${roomId}`);
    const dbLabel = document.getElementById(`dbLevel-${roomId}`);

    const minDb = -90;
    const percentage = 100 - Math.max(0, Math.min(100, ((level - 0) / minDb) * 100));

    if (meter) {
        meter.style.width = percentage + '%';
    }

    if (dbLabel) {
        dbLabel.textContent = level.toFixed(1) + ' dB';
    }
}

// ===== Navigate to Configure =====
function navigateToConfigure(roomId) {
    window.location.href = `/Home/Index?editRoom=${roomId}`;
}

// ===== Toast Message =====
function showMessage(message, isError = false) {
    const messageElement = document.getElementById('settingsMessage');
    if (!messageElement) return;

    messageElement.textContent = message;
    messageElement.className = 'settings-toast ' + (isError ? 'error' : 'success');
    messageElement.style.display = 'block';

    setTimeout(() => {
        messageElement.style.display = 'none';
    }, 3000);
}

// ===== PWA: Notification Permission =====
function pwaRequestNotificationPermission() {
    if (!('Notification' in window)) return;
    if (Notification.permission === 'default') {
        Notification.requestPermission().then((result) => {
            diagInfo("pwa.notification.permission", { result });
        });
    }
}

// ===== PWA: Sound Alert Handler =====
function handleSoundAlert(roomId, level, threshold) {
    const now = Date.now();
    if (pwaLastAlertTime[roomId] && (now - pwaLastAlertTime[roomId]) < PWA_ALERT_COOLDOWN_MS) {
        return; // Still in cooldown
    }
    pwaLastAlertTime[roomId] = now;

    const room = currentRooms.find(r => r.id === roomId);
    const roomName = room ? room.name : `Room ${roomId}`;

    // Visual alert — flash card
    const card = document.querySelector(`.dash-card[data-room-id="${roomId}"]`);
    if (card) {
        card.classList.add('alerting');
        setTimeout(() => card.classList.remove('alerting'), 2000);
    }

    // Vibrate (Android Chrome)
    if (navigator.vibrate) {
        navigator.vibrate([0, 200, 100, 200, 100, 400]);
    }

    // System notification via service worker
    pwaShowNotification(
        `Sound Alert \u2014 ${roomName}`,
        `Sound level at ${level.toFixed(1)} dB exceeds threshold (${threshold.toFixed(1)} dB)`,
        `sound-alert-${roomId}`
    );
}

// ===== PWA: Show Notification =====
function pwaShowNotification(title, body, tag) {
    if (!('Notification' in window) || Notification.permission !== 'granted') return;

    if (navigator.serviceWorker && navigator.serviceWorker.controller) {
        navigator.serviceWorker.controller.postMessage({
            type: 'SHOW_NOTIFICATION',
            title: title,
            body: body,
            tag: tag,
            icon: '/images/icon-192.png'
        });
    } else {
        // Fallback: direct notification (no service worker)
        try {
            new Notification(title, {
                body: body,
                icon: '/images/icon-192.png',
                tag: tag,
                silent: false
            });
        } catch (e) {
            console.warn('Notification failed:', e);
        }
    }
}

// ===== PWA: Persistent Monitoring State =====
function pwaSaveMonitoringState() {
    try {
        const ids = Array.from(monitoringRooms);
        localStorage.setItem(PWA_STORAGE_KEY, JSON.stringify(ids));
        diagInfo("pwa.state.saved", { roomIds: ids });
    } catch (e) {
        console.warn('Failed to save monitoring state:', e);
    }
}

function pwaLoadMonitoringState() {
    try {
        const stored = localStorage.getItem(PWA_STORAGE_KEY);
        if (!stored) return [];
        const ids = JSON.parse(stored);
        return Array.isArray(ids) ? ids : [];
    } catch (e) {
        console.warn('Failed to load monitoring state:', e);
        return [];
    }
}

let pwaAutoResumeExecuted = false;
function pwaAutoResumeMonitoring() {
    if (pwaAutoResumeExecuted) return;
    pwaAutoResumeExecuted = true;

    const savedIds = pwaLoadMonitoringState();
    if (savedIds.length === 0) return;

    const validRoomIds = new Set(currentRooms.map(r => r.id));
    const toResume = savedIds.filter(id => validRoomIds.has(id) && !monitoringRooms.has(id));

    if (toResume.length === 0) return;

    diagInfo("pwa.autoResume", { roomIds: toResume });
    for (const roomId of toResume) {
        startMonitoring(roomId);
    }
}

// ===== PWA: Screen Wake Lock =====
async function pwaAcquireWakeLock() {
    if (!('wakeLock' in navigator)) return;
    if (pwaWakeLock) return; // Already held

    try {
        pwaWakeLock = await navigator.wakeLock.request('screen');
        diagInfo("pwa.wakeLock.acquired");

        pwaWakeLock.addEventListener('release', () => {
            diagInfo("pwa.wakeLock.released");
            pwaWakeLock = null;
        });
    } catch (e) {
        diagWarn("pwa.wakeLock.failed", { error: normalizeError(e) });
    }
}

function pwaReleaseWakeLock() {
    if (pwaWakeLock) {
        pwaWakeLock.release();
        pwaWakeLock = null;
    }
}

// Re-acquire wake lock when tab becomes visible (required by API)
document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && monitoringRooms.size > 0) {
        pwaAcquireWakeLock();
    }
});

// ===== PWA: Media Session API =====
function pwaUpdateMediaSession() {
    if (!('mediaSession' in navigator)) return;

    if (monitoringRooms.size === 0) {
        navigator.mediaSession.metadata = null;
        navigator.mediaSession.playbackState = 'none';
        return;
    }

    const roomNames = Array.from(monitoringRooms)
        .map(id => currentRooms.find(r => r.id === id))
        .filter(Boolean)
        .map(r => r.name);

    navigator.mediaSession.metadata = new MediaMetadata({
        title: 'BabyMonitarr',
        artist: `Monitoring: ${roomNames.join(', ')}`,
        artwork: [
            { src: '/images/icon-192.png', sizes: '192x192', type: 'image/png' },
            { src: '/images/icon-512.png', sizes: '512x512', type: 'image/png' }
        ]
    });

    navigator.mediaSession.playbackState = 'playing';
}

// ===== PWA: Platform Detection =====
function pwaDetectPlatform() {
    const ua = navigator.userAgent || '';
    // iPadOS 13+ reports as MacIntel but has touch
    const isIOS = /iPhone|iPad|iPod/.test(ua) ||
        (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    if (isIOS) return 'ios';
    if (/Android/.test(ua)) return 'android';
    return 'desktop';
}

function pwaIsStandalone() {
    return window.matchMedia('(display-mode: standalone)').matches ||
           window.navigator.standalone === true;
}

// ===== PWA: Install Prompt =====
let pwaAndroidFallbackTimer = null;

window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    pwaInstallPrompt = e;
    // Cancel Android fallback timer since native prompt is available
    if (pwaAndroidFallbackTimer) {
        clearTimeout(pwaAndroidFallbackTimer);
        pwaAndroidFallbackTimer = null;
    }
    // Only show install banner on mobile/tablet, not desktop
    if (!pwaIsStandalone() && pwaDetectPlatform() !== 'desktop') {
        pwaShowInstallBanner('chromium');
    }
    diagInfo("pwa.installPrompt.captured");
});

window.addEventListener('appinstalled', () => {
    pwaInstallPrompt = null;
    pwaHideInstallBanner();
    diagInfo("pwa.installed");
});

// Show platform-appropriate install banner on page load
document.addEventListener('DOMContentLoaded', () => {
    if (pwaIsStandalone()) return;

    const platform = pwaDetectPlatform();
    diagInfo("pwa.platform.detected", { platform });

    if (platform === 'ios') {
        pwaShowInstallBanner('ios');
    } else if (platform === 'android') {
        // Wait briefly for beforeinstallprompt; show fallback if it doesn't fire
        pwaAndroidFallbackTimer = setTimeout(() => {
            if (!pwaInstallPrompt) {
                pwaShowInstallBanner('android-manual');
                diagInfo("pwa.installBanner.androidFallback");
            }
        }, 3000);
    }
    // Desktop: handled entirely by beforeinstallprompt event above
});

function pwaShowInstallBanner(variant) {
    const banner = document.getElementById('pwaInstallBanner');
    if (!banner) return;

    let html = '';
    if (variant === 'ios') {
        html = `
            <i class="fas fa-arrow-up-from-bracket" style="color: var(--accent-teal); font-size: 1.2rem;"></i>
            <div class="pwa-install-banner-text">
                <strong>Install BabyMonitarr</strong> for background audio, notifications, and a native app experience.
                <div class="pwa-install-steps">Tap <i class="fas fa-arrow-up-from-bracket"></i> Share, then <strong>Add to Home Screen</strong></div>
            </div>
            <button class="btn-pwa-dismiss" onclick="pwaHideInstallBanner()">&times;</button>
        `;
    } else if (variant === 'android-manual') {
        html = `
            <i class="fas fa-download" style="color: var(--accent-teal); font-size: 1.2rem;"></i>
            <div class="pwa-install-banner-text">
                <strong>Install BabyMonitarr</strong> for background audio, notifications, and a native app experience.
                <div class="pwa-install-steps">Tap <i class="fas fa-ellipsis-vertical"></i> menu, then <strong>Install app</strong> or <strong>Add to Home Screen</strong></div>
            </div>
            <button class="btn-pwa-dismiss" onclick="pwaHideInstallBanner()">&times;</button>
        `;
    } else {
        // chromium / desktop — has native install prompt
        html = `
            <i class="fas fa-download" style="color: var(--accent-teal); font-size: 1.2rem;"></i>
            <div class="pwa-install-banner-text">
                <strong>Install BabyMonitarr</strong> for background audio, notifications, and a native app experience.
            </div>
            <button class="btn-pwa-install" onclick="pwaInstallApp()">Install</button>
            <button class="btn-pwa-dismiss" onclick="pwaHideInstallBanner()">&times;</button>
        `;
    }

    banner.innerHTML = html;
    banner.style.display = '';
    diagInfo("pwa.installBanner.shown", { variant });
}

function pwaHideInstallBanner() {
    const banner = document.getElementById('pwaInstallBanner');
    if (banner) banner.style.display = 'none';
}

function pwaInstallApp() {
    if (!pwaInstallPrompt) return;
    pwaInstallPrompt.prompt();
    pwaInstallPrompt.userChoice.then((result) => {
        diagInfo("pwa.installPrompt.result", { outcome: result.outcome });
        pwaInstallPrompt = null;
        pwaHideInstallBanner();
    });
}
