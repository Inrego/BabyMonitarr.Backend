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

document.addEventListener('DOMContentLoaded', function () {
    try {
        initializeSignalRConnection();
    } catch (error) {
        console.error("Error initializing SignalR connection:", error);
    }
});

function initializeSignalRConnection() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl("/audioHub")
        .withAutomaticReconnect()
        .build();

    // Handle server ICE candidates (audio - per room)
    connection.on("ReceiveAudioIceCandidate", async (roomId, candidate, sdpMid, sdpMLineIndex) => {
        const candidateStr = candidate.startsWith('candidate:') ? candidate : `candidate:${candidate}`;
        const iceCandidate = new RTCIceCandidate({
            candidate: candidateStr,
            sdpMid: sdpMid,
            sdpMLineIndex: sdpMLineIndex
        });

        const pc = audioConnections[roomId];
        if (pc && pc.remoteDescription) {
            try {
                await pc.addIceCandidate(iceCandidate);
            } catch (err) {
                console.warn(`Could not add audio ICE candidate for room ${roomId}:`, err.message);
            }
        } else {
            if (!audioPendingCandidates[roomId]) {
                audioPendingCandidates[roomId] = [];
            }
            audioPendingCandidates[roomId].push(iceCandidate);
        }
    });

    // Handle server ICE candidates (video)
    connection.on("ReceiveVideoIceCandidate", async (roomId, candidate, sdpMid, sdpMLineIndex) => {
        const candidateStr = candidate.startsWith('candidate:') ? candidate : `candidate:${candidate}`;
        const iceCandidate = new RTCIceCandidate({
            candidate: candidateStr,
            sdpMid: sdpMid,
            sdpMLineIndex: sdpMLineIndex
        });

        const pc = videoConnections[roomId];
        if (pc && pc.remoteDescription) {
            try {
                await pc.addIceCandidate(iceCandidate);
            } catch (err) {
                console.warn(`Could not add video ICE candidate for room ${roomId}:`, err.message);
            }
        } else {
            if (!videoPendingCandidates[roomId]) {
                videoPendingCandidates[roomId] = [];
            }
            videoPendingCandidates[roomId].push(iceCandidate);
        }
    });

    // Handle room updates
    connection.on("RoomsUpdated", async () => {
        await loadRooms();
    });

    connection.start()
        .then(async () => {
            console.log("Dashboard SignalR Connected");
            await loadRooms();
        })
        .catch(err => {
            console.error(err);
            setTimeout(initializeSignalRConnection, 5000);
        });
}

async function loadRooms() {
    try {
        const previousRoomIds = new Set(currentRooms.map(r => r.id));
        currentRooms = await connection.invoke("GetRooms");
        const currentRoomIds = new Set(currentRooms.map(r => r.id));

        // Stop streams for rooms that were removed from config
        for (const roomId of monitoringRooms) {
            if (!currentRoomIds.has(roomId)) {
                await stopMonitoring(roomId, true);
            }
        }

        renderDashboard();
    } catch (err) {
        console.error("Error loading rooms:", err);
    }
}

// ===== Dashboard Rendering =====
function renderDashboard() {
    const grid = document.getElementById('dashboardGrid');
    const empty = document.getElementById('dashboardEmpty');
    if (!grid || !empty) return;

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
        videoEl.srcObject = new MediaStream([track]);
        videoEl.style.display = '';
        videoEl.play().catch(e => console.error(`Error replaying video for room ${roomId}:`, e));
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
    if (monitoringRooms.has(roomId)) return;
    const room = currentRooms.find(r => r.id === roomId);
    if (!room) return;

    monitoringRooms.add(roomId);
    updateRoomCard(roomId);

    const hasAudio = room.enableAudioStream && (room.cameraStreamUrl || room.nestDeviceId);
    const hasVideo = room.enableVideoStream && (room.cameraStreamUrl || room.nestDeviceId);

    // Start streams (audio unmuted by default — noise meter data arrives via data channel)
    if (hasAudio) {
        await startAudioStream(roomId);
        updateMuteButton(roomId);
    }
    if (hasVideo) {
        await startVideoStream(roomId);
    }
}

async function stopMonitoring(roomId, skipRender) {
    // Stop both streams
    if (audioConnections[roomId]) {
        await stopAudioStream(roomId);
    }
    if (videoConnections[roomId]) {
        await stopVideoStream(roomId);
    }

    monitoringRooms.delete(roomId);

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
    try {
        // Show loading indicator
        const loadingEl = document.getElementById(`videoLoading-${roomId}`);
        if (loadingEl) loadingEl.style.display = '';

        const offerSdp = await connection.invoke("StartVideoStream", roomId);
        if (!offerSdp) {
            console.error(`No SDP offer received for video room ${roomId}`);
            if (loadingEl) loadingEl.style.display = 'none';
            return;
        }

        const configuration = {
            iceServers: [
                { urls: 'stun:stun.l.google.com:19302' }
            ]
        };

        const pc = new RTCPeerConnection(configuration);
        videoConnections[roomId] = pc;

        // Handle ICE candidates
        pc.onicecandidate = async (event) => {
            if (event.candidate) {
                try {
                    await connection.invoke("AddVideoIceCandidate",
                        roomId,
                        event.candidate.candidate,
                        event.candidate.sdpMid,
                        event.candidate.sdpMLineIndex
                    );
                } catch (err) {
                    console.error(`Error sending video ICE candidate for room ${roomId}:`, err);
                }
            }
        };

        // Handle video track arrival
        pc.ontrack = (event) => {
            console.log(`Video track received for room ${roomId}`, event.track.kind);
            if (event.track.kind === 'video') {
                videoTracks[roomId] = event.track;

                const videoEl = document.getElementById(`video-${roomId}`);
                const iconEl = document.getElementById(`iconPlaceholder-${roomId}`);
                const loadingIndicator = document.getElementById(`videoLoading-${roomId}`);

                if (videoEl) {
                    videoEl.srcObject = event.streams[0] || new MediaStream([event.track]);
                    videoEl.style.display = '';
                    videoEl.play().catch(e => console.error(`Error playing video for room ${roomId}:`, e));
                }
                if (iconEl) iconEl.style.display = 'none';
                if (loadingIndicator) loadingIndicator.style.display = 'none';
            }
        };

        // Handle connection state changes
        pc.onconnectionstatechange = () => {
            const state = pc.connectionState;
            console.log(`Video connection state for room ${roomId}:`, state);

            if (state === 'disconnected' || state === 'failed' || state === 'closed') {
                onVideoDisconnected(roomId);
            }
        };

        // Set remote description (the offer from server)
        await pc.setRemoteDescription({ type: 'offer', sdp: offerSdp });

        // Create and send answer
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        await connection.invoke("SetVideoRemoteDescription", roomId, answer.type, answer.sdp);

        // Process queued ICE candidates
        if (videoPendingCandidates[roomId] && videoPendingCandidates[roomId].length > 0) {
            for (const candidate of videoPendingCandidates[roomId]) {
                try {
                    await pc.addIceCandidate(candidate);
                } catch (err) {
                    console.warn(`Could not add queued video ICE candidate for room ${roomId}:`, err.message);
                }
            }
            videoPendingCandidates[roomId] = [];
        }

    } catch (error) {
        console.error(`Error starting video stream for room ${roomId}:`, error);
        const loadingEl = document.getElementById(`videoLoading-${roomId}`);
        if (loadingEl) loadingEl.style.display = 'none';
    }
}

async function stopVideoStream(roomId) {
    const pc = videoConnections[roomId];
    if (pc) {
        try {
            await connection.invoke("StopVideoStream", roomId);
        } catch (err) {
            console.error(`Error stopping video stream for room ${roomId}:`, err);
        }
        pc.close();
        delete videoConnections[roomId];
    }
    delete videoPendingCandidates[roomId];
    delete videoTracks[roomId];
    onVideoDisconnected(roomId);
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
    try {
        const offerSdp = await connection.invoke("StartAudioStream", roomId);
        if (!offerSdp) {
            console.error(`No SDP offer received for audio room ${roomId}`);
            return;
        }

        const configuration = {
            iceServers: [
                { urls: 'stun:stun.l.google.com:19302' }
            ]
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

        // Handle ICE candidates
        pc.onicecandidate = async (event) => {
            if (event.candidate) {
                try {
                    await connection.invoke("AddAudioIceCandidate",
                        roomId,
                        event.candidate.candidate,
                        event.candidate.sdpMid,
                        event.candidate.sdpMLineIndex
                    );
                } catch (err) {
                    console.error(`Error sending audio ICE candidate for room ${roomId}:`, err);
                }
            }
        };

        // Handle data channel (audio levels, sound alerts)
        pc.ondatachannel = (event) => {
            const dataChannel = event.channel;
            dataChannel.onmessage = (event) => {
                try {
                    const message = JSON.parse(event.data);
                    if (message.type === 'audioLevel') {
                        updateCardMeter(roomId, message.level);
                    } else if (message.type === 'soundAlert') {
                        console.log(`Sound alert for room ${roomId}: ${message.level.toFixed(1)} dB (threshold: ${message.threshold.toFixed(1)} dB)`);
                    }
                } catch (err) {
                    console.error("Error parsing data channel message:", err);
                }
            };
        };

        // Handle audio track arrival
        pc.ontrack = (event) => {
            if (event.track.kind === 'audio' && audioEl) {
                audioEl.srcObject = event.streams[0] || new MediaStream([event.track]);
                audioEl.play().catch(e => {
                    // Autoplay policy may block unmuted playback — fall back to muted
                    console.warn(`Autoplay blocked for room ${roomId}, falling back to muted:`, e.message);
                    audioEl.muted = true;
                    audioEl.play().catch(e2 => console.error(`Error playing audio for room ${roomId}:`, e2));
                    updateMuteButton(roomId);
                });
            }
        };

        // Handle connection state changes
        pc.onconnectionstatechange = () => {
            const state = pc.connectionState;
            console.log(`Audio connection state for room ${roomId}:`, state);

            if (state === 'disconnected' || state === 'failed' || state === 'closed') {
                onAudioDisconnected(roomId);
            }
        };

        // Set remote description (the offer from server)
        await pc.setRemoteDescription({ type: 'offer', sdp: offerSdp });

        // Create and send answer
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        await connection.invoke("SetAudioRemoteDescription", roomId, answer.type, answer.sdp);

        // Process queued ICE candidates
        if (audioPendingCandidates[roomId] && audioPendingCandidates[roomId].length > 0) {
            for (const candidate of audioPendingCandidates[roomId]) {
                try {
                    await pc.addIceCandidate(candidate);
                } catch (err) {
                    console.warn(`Could not add queued audio ICE candidate for room ${roomId}:`, err.message);
                }
            }
            audioPendingCandidates[roomId] = [];
        }

    } catch (error) {
        console.error(`Error starting audio stream for room ${roomId}:`, error);
        showMessage("Error starting audio stream", true);
    }
}

async function stopAudioStream(roomId) {
    const pc = audioConnections[roomId];
    if (pc) {
        try {
            await connection.invoke("StopAudioStream", roomId);
        } catch (err) {
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
