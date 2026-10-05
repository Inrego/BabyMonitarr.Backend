// Custom Cast Web Receiver: plays one room over WebRTC for sub-second delay.
//
// The page is hosted centrally (GitHub Pages) so one published app serves every installation.
// The server launches it on the cast device, then sends a one-time token and its own URL on
// NAMESPACE. The page presents that token to <serverUrl>/cast/receiverHub, which only ever unlocks that room's streams,
// and negotiates one server-offered peer per stream kind, exactly like the dashboard does.
// Recovery is the page's own job: the server only watches whether the app is still running.

const NAMESPACE = "urn:x-cast:com.babymonitarr.receiver";

// A peer stuck in "disconnected" this long is renegotiated rather than waited on.
const DISCONNECTED_GRACE_MS = 5000;
const RESTART_DELAY_MS = 2000;

const videoEl = document.getElementById("video");
const statusEl = document.getElementById("status");
const overlayEl = document.getElementById("overlay");
// Attached to the video only once a track arrives: a Nest Hub never acknowledges CAF's "ready"
// from a page whose video already holds a MediaStream, so the launch hangs on "Starting…".
const mediaStream = new MediaStream();

let token = null;
let serverUrl = null;       // null: the page is served by the backend itself
let joined = null;          // CastReceiverJoinResult for the current connection
let connection = null;
let ended = false;
const peers = {};           // kind -> RTCPeerConnection
const pendingCandidates = {}; // kind -> server candidates that arrived before the offer was applied
const restartTimers = {};   // kind -> timeout id
let overlayTimer = null;

// The app's five-minute sound level graph, drawn over the video when the server's global
// "sound graph on cast" setting is on. Levels come from the audio peer's data channel, on the
// app's scale and zones: -90..0 dB shown as 0..100, colour changing at 40 and 55.
const GRAPH_WINDOW_MS = 5 * 60 * 1000;
const GRAPH_SAMPLE_MS = 500;
// Samples further apart than this mean the audio dropped; the line breaks rather than bridging it.
const GRAPH_GAP_MS = 3000;
const GRAPH_MIN_DB = -90;
const GRAPH_ZONES = [40, 55];
const GRAPH_COLORS = ["#88D5C3", "#FFB088", "#FF8B94"];
const graphEl = document.getElementById("soundGraph");
const graphHistory = [];    // { t, v } with v on the 0..100 display scale, oldest first
let graphShown = false;

function setStatus(text) {
    statusEl.textContent = text || "";
}

function showRoomName(name) {
    overlayEl.textContent = name;
    overlayEl.classList.remove("faded");
    clearTimeout(overlayTimer);
    overlayTimer = setTimeout(() => overlayEl.classList.add("faded"), 5000);
}

function displayLevel(db) {
    const clamped = Math.min(0, Math.max(GRAPH_MIN_DB, db));
    return ((clamped - GRAPH_MIN_DB) / -GRAPH_MIN_DB) * 100;
}

function onAudioLevel(db) {
    const now = Date.now();
    const last = graphHistory[graphHistory.length - 1];
    if (last && now - last.t < GRAPH_SAMPLE_MS) return;
    graphHistory.push({ t: now, v: displayLevel(db) });
    while (graphHistory[0].t < now - GRAPH_WINDOW_MS) graphHistory.shift();
    drawSoundGraph();
}

function applySoundGraph() {
    graphShown = !!(joined?.soundGraph && joined.audio);
    graphEl.hidden = !graphShown;
    document.body.classList.toggle("sound-graph", graphShown);
    drawSoundGraph();
}

function zoneColor(v) {
    const zone = GRAPH_ZONES.findIndex((limit) => v < limit);
    return GRAPH_COLORS[zone === -1 ? GRAPH_ZONES.length : zone];
}

function drawSoundGraph() {
    if (!graphShown) return;
    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(graphEl.clientWidth * dpr);
    const h = Math.round(graphEl.clientHeight * dpr);
    if (graphEl.width !== w || graphEl.height !== h) {
        graphEl.width = w;
        graphEl.height = h;
    }
    const ctx = graphEl.getContext("2d");
    ctx.clearRect(0, 0, w, h);

    const now = Date.now();
    const pad = 10 * dpr;
    const x = (t) => (w - pad) * (1 - (now - t) / GRAPH_WINDOW_MS);
    const y = (v) => pad + (h - 2 * pad) * (1 - v / 100);

    ctx.lineWidth = 1.5 * dpr;
    ctx.setLineDash([6 * dpr, 6 * dpr]);
    ctx.strokeStyle = "rgba(255, 255, 255, 0.4)";
    for (const limit of GRAPH_ZONES) {
        ctx.beginPath();
        ctx.moveTo(0, y(limit));
        ctx.lineTo(w, y(limit));
        ctx.stroke();
    }
    ctx.setLineDash([]);
    if (graphHistory.length === 0) return;

    // Smoothed through the midpoints between samples, the way the app's curved line reads.
    ctx.beginPath();
    graphHistory.forEach((p, i) => {
        const prev = graphHistory[i - 1];
        if (!prev || p.t - prev.t > GRAPH_GAP_MS) {
            ctx.moveTo(x(p.t), y(p.v));
        } else {
            const mx = (x(prev.t) + x(p.t)) / 2;
            const my = (y(prev.v) + y(p.v)) / 2;
            ctx.quadraticCurveTo(x(prev.t), y(prev.v), mx, my);
            if (i === graphHistory.length - 1) ctx.lineTo(x(p.t), y(p.v));
        }
    });
    ctx.lineJoin = "round";
    ctx.lineCap = "round";

    // A dark halo first, so the pastel line stays visible on a white picture.
    ctx.strokeStyle = "rgba(0, 0, 0, 0.55)";
    ctx.lineWidth = 8 * dpr;
    ctx.stroke();

    // Coloured by height with hard stops at the zone limits: the app's per-zone segments.
    const gradient = ctx.createLinearGradient(0, y(0), 0, y(100));
    let from = 0;
    GRAPH_COLORS.forEach((color, i) => {
        const to = i < GRAPH_ZONES.length ? GRAPH_ZONES[i] / 100 : 1;
        gradient.addColorStop(from, color);
        gradient.addColorStop(to, color);
        from = to;
    });
    ctx.strokeStyle = gradient;
    ctx.lineWidth = 4 * dpr;
    ctx.stroke();

    const head = graphHistory[graphHistory.length - 1];
    ctx.beginPath();
    ctx.arc(x(head.t), y(head.v), 6 * dpr, 0, 2 * Math.PI);
    ctx.fillStyle = zoneColor(head.v);
    ctx.fill();
    ctx.strokeStyle = "rgba(0, 0, 0, 0.55)";
    ctx.lineWidth = 2.5 * dpr;
    ctx.stroke();
}

window.addEventListener("resize", drawSoundGraph);

function normalizeIceServers(servers) {
    return (servers || [])
        .filter((s) => s && typeof s.urls === "string" && s.urls.trim())
        .map((s) => {
            const entry = { urls: s.urls.trim() };
            if (s.username) entry.username = s.username;
            if (s.credential) entry.credential = s.credential;
            return entry;
        });
}

function isEndedError(err) {
    return /cast session has ended/i.test(err?.message || "");
}

// Stopping a cast closes the app, back to the device's own screen. The server tells the page
// directly because its cast-channel STOP does not always reach the device.
function onSessionEnded() {
    if (ended) return;
    ended = true;
    Object.keys(peers).forEach(closePeer);
    setStatus("Casting ended");
    clearTimeout(keepAliveTimer);
    void connection?.stop();
    context.stop();
}

function closePeer(kind) {
    clearTimeout(restartTimers[kind]);
    delete restartTimers[kind];
    const pc = peers[kind];
    delete peers[kind];
    if (pc) {
        try { pc.close(); } catch { /* already closed */ }
    }
    for (const track of mediaStream.getTracks().filter((t) => t.kind === kind)) {
        mediaStream.removeTrack(track);
    }
}

function scheduleRestart(kind, delayMs) {
    if (ended || restartTimers[kind]) return;
    restartTimers[kind] = setTimeout(() => {
        delete restartTimers[kind];
        void startStream(kind);
    }, delayMs);
}

async function startStream(kind) {
    if (ended || !connection || connection.state !== signalR.HubConnectionState.Connected) return;
    closePeer(kind);
    pendingCandidates[kind] = [];

    let pc;
    try {
        const offerSdp = await connection.invoke("StartStream", kind);
        pc = new RTCPeerConnection({ iceServers: normalizeIceServers(joined?.iceServers) });
        peers[kind] = pc;

        pc.onicecandidate = (event) => {
            if (!event.candidate || peers[kind] !== pc) return;
            connection.invoke(
                "AddIceCandidate",
                kind,
                event.candidate.candidate,
                event.candidate.sdpMid,
                event.candidate.sdpMLineIndex
            ).catch((err) => console.warn(`[receiver] ${kind} ICE send failed`, err));
        };

        pc.ontrack = (event) => {
            if (peers[kind] !== pc) return;
            for (const track of mediaStream.getTracks().filter((t) => t.kind === event.track.kind)) {
                mediaStream.removeTrack(track);
            }
            mediaStream.addTrack(event.track);
            if (videoEl.srcObject !== mediaStream) videoEl.srcObject = mediaStream;
            void play();
            if (event.track.kind === "video") setStatus("");
        };

        // The server's audio peer carries the room's levels, as it does for the dashboard.
        pc.ondatachannel = (event) => {
            event.channel.onmessage = (msg) => {
                if (peers[kind] !== pc) return;
                try {
                    const message = JSON.parse(msg.data);
                    if (message.type === "audioLevel") onAudioLevel(message.level);
                } catch {
                    /* not a level message */
                }
            };
        };

        pc.onconnectionstatechange = () => {
            if (peers[kind] !== pc) return;
            const state = pc.connectionState;
            console.log(`[receiver] ${kind} connection ${state}`);
            if (state === "connected") {
                clearTimeout(restartTimers[kind]);
                delete restartTimers[kind];
            } else if (state === "failed") {
                scheduleRestart(kind, RESTART_DELAY_MS);
            } else if (state === "disconnected") {
                scheduleRestart(kind, DISCONNECTED_GRACE_MS);
            }
        };

        await pc.setRemoteDescription({ type: "offer", sdp: offerSdp });
        for (const candidate of pendingCandidates[kind].splice(0)) {
            await pc.addIceCandidate(candidate).catch(() => { /* stale candidate */ });
        }

        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        await connection.invoke("SetAnswer", kind, answer.sdp);
    } catch (err) {
        if (isEndedError(err)) {
            onSessionEnded();
            return;
        }
        console.warn(`[receiver] ${kind} stream failed to start`, err);
        if (peers[kind] === pc) closePeer(kind);
        if (kind === "video") setStatus("Reconnecting…");
        scheduleRestart(kind, RESTART_DELAY_MS);
    }
}

// Cast devices allow unmuted autoplay; if one ever refuses, a silent picture beats a frozen one.
async function play() {
    try {
        await videoEl.play();
    } catch (err) {
        if (err?.name !== "NotAllowedError" || videoEl.muted) {
            console.warn("[receiver] play failed", err);
            return;
        }
        console.warn("[receiver] unmuted autoplay refused; playing muted", err);
        videoEl.muted = true;
        showRoomName(`${joined?.roomName ?? ""} (sound blocked by the device)`);
        await videoEl.play().catch((e) => console.warn("[receiver] muted play failed", e));
    }
}

async function addServerCandidate(kind, candidate, sdpMid, sdpMLineIndex) {
    // The server sends SIPSorcery's candidate without the "candidate:" prefix, which Chrome rejects.
    const line = candidate.startsWith("candidate:") ? candidate : `candidate:${candidate}`;
    const init = { candidate: line, sdpMid, sdpMLineIndex };
    const pc = peers[kind];
    if (!pc || !pc.remoteDescription) {
        (pendingCandidates[kind] ||= []).push(init);
        return;
    }
    await pc.addIceCandidate(init).catch((err) => console.warn(`[receiver] ${kind} ICE add failed`, err));
}

// A new hub connection id means the server dropped every peer of the old one, so everything is
// renegotiated from the join.
async function joinAndStream() {
    try {
        joined = await connection.invoke("Join", token);
    } catch (err) {
        if (isEndedError(err)) {
            onSessionEnded();
            return;
        }
        throw err;
    }

    showRoomName(joined.roomName);
    applySoundGraph();
    Object.keys(peers).forEach(closePeer);
    if (joined.video) void startStream("video");
    if (joined.audio) void startStream("audio");
}

function hubUrl() {
    return serverUrl ? `${serverUrl.replace(/\/+$/, "")}/cast/receiverHub` : "receiverHub";
}

async function connect() {
    const own = new signalR.HubConnectionBuilder()
        // No cookies: the token is the only credential, and the hub allows any origin, which a
        // credentialed request would not be allowed to use.
        .withUrl(hubUrl(), { withCredentials: false })
        .withAutomaticReconnect(SIGNALR_RETRY_POLICY)
        .build();
    connection = own;

    connection.on("IceCandidate", (kind, candidate, sdpMid, sdpMLineIndex) =>
        void addServerCandidate(kind, candidate, sdpMid, sdpMLineIndex));
    connection.on("PeerClosed", (kind) => scheduleRestart(kind, RESTART_DELAY_MS));
    connection.on("SessionEnded", onSessionEnded);
    connection.on("SoundGraph", (enabled) => {
        if (!joined) return;
        joined.soundGraph = enabled;
        applySoundGraph();
    });

    connection.onreconnecting(() => setStatus("Reconnecting…"));
    connection.onreconnected(() => {
        joinAndStream().catch((err) => console.warn("[receiver] rejoin failed", err));
    });

    // The first start is not covered by automatic reconnect, so retry it here forever.
    for (let attempt = 0; !ended && connection === own; attempt++) {
        try {
            await connection.start();
            await joinAndStream();
            return;
        } catch (err) {
            console.warn("[receiver] connect failed", err);
            setStatus("Connecting…");
            await new Promise((resolve) => setTimeout(resolve, signalrRetryDelay(attempt + 1)));
        }
    }
}

const context = cast.framework.CastReceiverContext.getInstance();

context.addCustomMessageListener(NAMESPACE, (event) => {
    const message = event.data;
    if (message?.type !== "join" || !message.token) return;
    // A recovering server re-sends the same token; only a new one means a new session.
    if (message.token === token) return;

    const newServer = (message.serverUrl || null) !== serverUrl;
    token = message.token;
    serverUrl = message.serverUrl || null;
    graphHistory.length = 0;
    ended = false;
    setStatus("Connecting…");
    if (!connection || newServer) {
        const old = connection;
        connection = null;
        Object.keys(peers).forEach(closePeer);
        if (old) void old.stop();
        void connect();
    } else if (connection.state === signalR.HubConnectionState.Connected) {
        void joinAndStream();
    }
});

// Nest Hubs send an app with no media session back to their dashboard 10 minutes after launch,
// and re-launching plays the Hub's connection chime. So the page "plays" a still image through
// the hidden CAF player and reloads it before the limit: a LOAD inside the running app, which
// keeps the session alive with no reload and no chime (the trick Home Assistant's receiver uses).
// The server ignores this media session; only the receiver status tells it the app is up.
const KEEPALIVE_URL = new URL("keepalive.png", location.href).href;
const KEEPALIVE_INTERVAL_MS = 9 * 60 * 1000;
const playerManager = context.getPlayerManager();
let keepAliveTimer = null;

function keepAlive() {
    clearTimeout(keepAliveTimer);
    const request = new cast.framework.messages.LoadRequestData();
    request.autoplay = true;
    request.media = new cast.framework.messages.MediaInformation();
    request.media.contentId = KEEPALIVE_URL;
    request.media.contentType = "image/png";
    request.media.streamType = cast.framework.messages.StreamType.NONE;
    request.media.metadata = new cast.framework.messages.GenericMediaMetadata();
    request.media.metadata.title = joined?.roomName || "BabyMonitarr";
    playerManager.load(request).catch((err) => console.warn("[receiver] keepalive load failed", err));
    keepAliveTimer = setTimeout(keepAlive, KEEPALIVE_INTERVAL_MS);
}

// The hidden player is only for the keepalive; anything else a sender loads would play unseen.
playerManager.setMessageInterceptor(cast.framework.messages.MessageType.LOAD, (request) => {
    if (request.media?.contentId === KEEPALIVE_URL) return request;
    const error = new cast.framework.messages.ErrorData(cast.framework.messages.ErrorType.LOAD_FAILED);
    error.reason = cast.framework.messages.ErrorReason.NOT_SUPPORTED;
    return error;
});

// Stopping the keepalive media from the device's controls means stopping the cast, as it did
// before the page had a media session: closing the app is what the server watches for.
playerManager.setMessageInterceptor(cast.framework.messages.MessageType.STOP, () => {
    context.stop();
    return null;
});

context.addEventListener(cast.framework.system.EventType.READY, () => {
    // Touch displays draw the player's controls over the page; the picture is all there is to see.
    const controls = document.querySelector("touch-controls");
    if (controls) controls.style.display = "none";
    keepAlive();
});

context.start({
    customNamespaces: { [NAMESPACE]: cast.framework.system.MessageType.JSON },
    // The keepalive image has no end, but nothing should ever close the app as idle.
    disableIdleTimeout: true,
    statusText: "BabyMonitarr"
});
