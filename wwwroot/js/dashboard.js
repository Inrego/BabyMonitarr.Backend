// SignalR connection
let connection;

// Video state
let videoConnections = {};        // { roomId: RTCPeerConnection }
let videoPendingCandidates = {};  // { roomId: [RTCIceCandidate] }

// Audio state (per-room)
let audioConnections = {};        // { roomId: RTCPeerConnection }
let audioPendingCandidates = {};  // { roomId: [RTCIceCandidate] }
let audioElements = {};           // { roomId: HTMLAudioElement }
let mutedRooms = {};              // { roomId: boolean }

// State
let currentRooms = [];
let activeRoomId = null;

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

    // Handle active room changes
    connection.on("ActiveRoomChanged", (room) => {
        activeRoomId = room.id;
        updateDashboardState();
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
        const previousRooms = currentRooms;
        currentRooms = await connection.invoke("GetRooms");
        const activeRoom = currentRooms.find(r => r.isActive);
        activeRoomId = activeRoom ? activeRoom.id : null;
        renderDashboard();

        // Manage video and audio streams after rendering
        await manageVideoStreams(previousRooms);
        await manageAudioStreams(previousRooms);
    } catch (err) {
        console.error("Error loading rooms:", err);
    }
}

// ===== Dashboard State Update (surgical, preserves DOM/video) =====
function updateDashboardState() {
    for (const room of currentRooms) {
        const isListening = !!audioConnections[room.id];
        const isMuted = !!mutedRooms[room.id];

        // Update LIVE badge
        const badge = document.getElementById(`liveBadge-${room.id}`);
        if (badge) {
            badge.classList.toggle('visible', isListening);
        }

        // Update Listen button
        const card = document.querySelector(`.dash-card[data-room-id="${room.id}"]`);
        if (!card) continue;
        const listenBtn = card.querySelector('.btn-dash-listen');
        if (listenBtn) {
            listenBtn.className = `btn-dash-action btn-dash-listen ${isListening ? 'active' : ''}`;
            listenBtn.innerHTML = `<i class="fas fa-${isListening ? 'stop' : 'headphones'}"></i> ${isListening ? 'Stop' : 'Listen'}`;
        }

        // Update Audio button
        const audioBtn = card.querySelector('.btn-dash-audio');
        if (audioBtn) {
            audioBtn.className = `btn-dash-action btn-dash-audio ${isListening ? '' : 'disabled'}`;
            audioBtn.disabled = !isListening;
            audioBtn.innerHTML = `<i class="fas fa-${isMuted && isListening ? 'volume-mute' : 'volume-up'}"></i> ${isMuted && isListening ? 'Muted' : 'Audio'}`;
        }
    }
}

// ===== Dashboard Rendering (full rebuild) =====
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
        const isListening = !!audioConnections[room.id];
        const isMuted = !!mutedRooms[room.id];
        const hasVideo = room.enableVideoStream && room.cameraStreamUrl;
        const hasAudio = room.enableAudioStream && room.cameraStreamUrl;
        return `
            <div class="dash-card" data-room-id="${room.id}">
                <div class="dash-card-preview">
                    <video id="video-${room.id}" class="dash-card-video" autoplay muted playsinline
                           style="display: none;"></video>
                    <div id="iconPlaceholder-${room.id}" class="dash-card-icon-placeholder">
                        <i class="fas fa-${escapeHtml(room.icon || 'baby')}"></i>
                    </div>
                    <div id="videoLoading-${room.id}" class="dash-card-video-loading" style="display: none;">
                        <i class="fas fa-spinner fa-spin"></i>
                    </div>
                    <span id="liveBadge-${room.id}" class="dash-card-live-badge ${isListening ? 'visible' : ''}">LIVE</span>
                    <span class="dash-card-room-name">${escapeHtml(room.name)}</span>
                    <span id="dbLevel-${room.id}" class="dash-card-db-level">--.- dB</span>
                    <div class="dash-card-meter-bar">
                        <div id="meter-${room.id}" class="dash-card-meter-fill"></div>
                    </div>
                </div>
                <div class="dash-card-actions">
                    <button class="btn-dash-action btn-dash-listen ${isListening ? 'active' : ''}" onclick="toggleListen(${room.id})" ${!hasAudio ? 'disabled' : ''}>
                        <i class="fas fa-${isListening ? 'stop' : 'headphones'}"></i>
                        ${isListening ? 'Stop' : 'Listen'}
                    </button>
                    <button class="btn-dash-action btn-dash-audio ${isListening ? '' : 'disabled'}" onclick="toggleMute(${room.id})" ${isListening ? '' : 'disabled'}>
                        <i class="fas fa-${isMuted && isListening ? 'volume-mute' : 'volume-up'}"></i>
                        ${isMuted && isListening ? 'Muted' : 'Audio'}
                    </button>
                    <button class="btn-dash-action btn-dash-configure" onclick="navigateToConfigure(${room.id})">
                        <i class="fas fa-cog"></i>
                        Configure
                    </button>
                </div>
            </div>
        `;
    }).join('');
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// ===== Video Stream Management =====
async function manageVideoStreams(previousRooms) {
    const videoRooms = currentRooms.filter(r => r.enableVideoStream && r.cameraStreamUrl);
    const videoRoomIds = new Set(videoRooms.map(r => r.id));

    // Stop video for rooms that were removed or had video disabled
    for (const roomId of Object.keys(videoConnections).map(Number)) {
        if (!videoRoomIds.has(roomId)) {
            await stopVideoStream(roomId);
        }
    }

    // Start video for new rooms that need it
    for (const room of videoRooms) {
        if (!videoConnections[room.id]) {
            await startVideoStream(room.id);
        }
    }
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

async function stopAllVideoStreams() {
    for (const roomId of Object.keys(videoConnections).map(Number)) {
        await stopVideoStream(roomId);
    }
}

// ===== Audio Stream Management (per-room) =====
async function manageAudioStreams(previousRooms) {
    const audioRooms = currentRooms.filter(r => r.enableAudioStream && r.cameraStreamUrl);
    const audioRoomIds = new Set(audioRooms.map(r => r.id));

    // Stop audio for rooms that were removed or had audio disabled
    for (const roomId of Object.keys(audioConnections).map(Number)) {
        if (!audioRoomIds.has(roomId)) {
            await stopAudioStream(roomId);
        }
    }
}

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

        // Create audio element for this room
        const audioEl = document.createElement('audio');
        audioEl.setAttribute('autoplay', 'true');
        audioEl.setAttribute('playsinline', 'true');
        audioEl.style.display = 'none';
        audioEl.muted = !!mutedRooms[roomId];
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
                audioEl.play().catch(e => console.error(`Error playing audio for room ${roomId}:`, e));
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

        updateDashboardState();
        const room = currentRooms.find(r => r.id === roomId);
        showMessage(`Listening to: ${room ? room.name : `Room ${roomId}`}`);

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

    delete mutedRooms[roomId];
    onAudioDisconnected(roomId);
}

function onAudioDisconnected(roomId) {
    // Reset meter
    const meter = document.getElementById(`meter-${roomId}`);
    if (meter) meter.style.width = '0%';
    const dbLabel = document.getElementById(`dbLevel-${roomId}`);
    if (dbLabel) dbLabel.textContent = '--.- dB';

    updateDashboardState();
}

async function stopAllAudioStreams() {
    for (const roomId of Object.keys(audioConnections).map(Number)) {
        await stopAudioStream(roomId);
    }
}

// ===== Listen / Mute Toggles (per-room) =====
async function toggleListen(roomId) {
    if (audioConnections[roomId]) {
        // Stop listening to this room
        await stopAudioStream(roomId);
        return;
    }

    // Start listening to this room
    await startAudioStream(roomId);
}

function toggleMute(roomId) {
    if (!audioConnections[roomId] || !audioElements[roomId]) return;

    mutedRooms[roomId] = !mutedRooms[roomId];
    audioElements[roomId].muted = mutedRooms[roomId];
    updateDashboardState();
}

// ===== Card Meter Updates =====
function updateCardMeter(roomId, level) {
    const meter = document.getElementById(`meter-${roomId}`);
    const dbLabel = document.getElementById(`dbLevel-${roomId}`);
    const badge = document.getElementById(`liveBadge-${roomId}`);

    const minDb = -90;
    const percentage = 100 - Math.max(0, Math.min(100, ((level - 0) / minDb) * 100));

    if (meter) {
        meter.style.width = percentage + '%';
    }

    if (dbLabel) {
        dbLabel.textContent = level.toFixed(1) + ' dB';
    }

    if (badge) {
        badge.classList.add('visible');
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
