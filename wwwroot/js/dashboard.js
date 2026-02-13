// SignalR connection
let connection;
// WebRTC peer connection
let peerConnection;
// Audio element for playback
let audioElement;
// Queue for ICE candidates
let pendingIceCandidates = [];

// State
let currentRooms = [];
let activeRoomId = null;
let listeningRoomId = null;
let isMuted = false;

document.addEventListener('DOMContentLoaded', function () {
    // Create audio element for playback
    audioElement = document.createElement('audio');
    audioElement.setAttribute('autoplay', 'true');
    audioElement.setAttribute('playsinline', 'true');
    audioElement.style.display = 'none';
    document.body.appendChild(audioElement);

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

    // Handle server ICE candidates
    connection.on("ReceiveIceCandidate", async (candidate, sdpMid, sdpMLineIndex) => {
        const candidateStr = candidate.startsWith('candidate:') ? candidate : `candidate:${candidate}`;
        const iceCandidate = new RTCIceCandidate({
            candidate: candidateStr,
            sdpMid: sdpMid,
            sdpMLineIndex: sdpMLineIndex
        });

        if (peerConnection && peerConnection.remoteDescription) {
            try {
                await peerConnection.addIceCandidate(iceCandidate);
            } catch (err) {
                console.warn("Could not add server ICE candidate:", err.message);
            }
        } else {
            pendingIceCandidates.push(iceCandidate);
        }
    });

    // Handle room updates
    connection.on("RoomsUpdated", async () => {
        await loadRooms();
    });

    // Handle active room changes
    connection.on("ActiveRoomChanged", (room) => {
        activeRoomId = room.id;
        renderDashboard();
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
        currentRooms = await connection.invoke("GetRooms");
        const activeRoom = currentRooms.find(r => r.isActive);
        activeRoomId = activeRoom ? activeRoom.id : null;
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
        const isListening = room.id === listeningRoomId;
        const isActive = room.isActive;
        return `
            <div class="dash-card" data-room-id="${room.id}">
                <div class="dash-card-preview">
                    <div class="dash-card-icon-placeholder">
                        <i class="fas fa-${escapeHtml(room.icon || 'baby')}"></i>
                    </div>
                    <span id="liveBadge-${room.id}" class="dash-card-live-badge ${isListening ? 'visible' : ''}">LIVE</span>
                    <span class="dash-card-room-name">${escapeHtml(room.name)}</span>
                    <span id="dbLevel-${room.id}" class="dash-card-db-level">--.- dB</span>
                    <div class="dash-card-meter-bar">
                        <div id="meter-${room.id}" class="dash-card-meter-fill"></div>
                    </div>
                </div>
                <div class="dash-card-actions">
                    <button class="btn-dash-action btn-dash-listen ${isListening ? 'active' : ''}" onclick="toggleListen(${room.id})">
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

// ===== Listen / WebRTC =====
async function toggleListen(roomId) {
    if (listeningRoomId === roomId) {
        // Stop listening to this room
        await stopStream();
        return;
    }

    // If listening to a different room, stop first
    if (listeningRoomId !== null) {
        await stopStream();
    }

    // Start listening to the new room
    try {
        // Activate this room on the server
        const room = await connection.invoke("SelectRoom", roomId);
        if (room) {
            activeRoomId = room.id;
        }

        // Start WebRTC stream
        const offerSdp = await connection.invoke("StartWebRtcStream");

        const configuration = {
            iceServers: [
                { urls: 'stun:stun.l.google.com:19302' }
            ]
        };

        peerConnection = new RTCPeerConnection(configuration);
        setupPeerConnectionHandlers(roomId);

        await peerConnection.setRemoteDescription({ type: 'offer', sdp: offerSdp });

        const answer = await peerConnection.createAnswer();
        await peerConnection.setLocalDescription(answer);

        await connection.invoke("SetRemoteDescription", answer.type, answer.sdp);

        // Process queued ICE candidates
        if (pendingIceCandidates.length > 0) {
            for (const candidate of pendingIceCandidates) {
                try {
                    await peerConnection.addIceCandidate(candidate);
                } catch (err) {
                    console.warn("Could not add queued ICE candidate:", err.message);
                }
            }
            pendingIceCandidates = [];
        }

        listeningRoomId = roomId;
        isMuted = false;
        renderDashboard();
        showMessage(`Listening to: ${room.name}`);
    } catch (error) {
        console.error("Error starting stream:", error);
        showMessage("Error starting audio stream", true);
    }
}

function setupPeerConnectionHandlers(roomId) {
    peerConnection.onicecandidate = async (event) => {
        if (event.candidate) {
            try {
                await connection.invoke("AddIceCandidate",
                    event.candidate.candidate,
                    event.candidate.sdpMid,
                    event.candidate.sdpMLineIndex
                );
            } catch (err) {
                console.error("Error sending ICE candidate:", err);
            }
        }
    };

    peerConnection.ondatachannel = (event) => {
        const dataChannel = event.channel;

        dataChannel.onmessage = (event) => {
            try {
                const message = JSON.parse(event.data);
                if (message.type === 'audioLevel') {
                    updateCardMeter(roomId, message.level);
                }
            } catch (err) {
                console.error("Error parsing data channel message:", err);
            }
        };
    };

    peerConnection.onconnectionstatechange = () => {
        const state = peerConnection.connectionState;
        console.log("WebRTC connection state:", state);

        if (state === 'disconnected' || state === 'failed' || state === 'closed') {
            if (audioElement) {
                audioElement.srcObject = null;
            }
            listeningRoomId = null;
            isMuted = false;
            renderDashboard();
        }
    };

    peerConnection.ontrack = (event) => {
        if (event.streams && event.streams[0] && audioElement) {
            audioElement.srcObject = event.streams[0];
            audioElement.play().catch(e => console.error("Error playing audio:", e));
        }
    };
}

async function stopStream() {
    pendingIceCandidates = [];

    if (peerConnection) {
        try {
            await connection.invoke("StopWebRtcStream");
            peerConnection.close();
            peerConnection = null;
        } catch (error) {
            console.error("Error stopping stream:", error);
        }
    }

    if (audioElement) {
        audioElement.srcObject = null;
    }

    listeningRoomId = null;
    isMuted = false;
    renderDashboard();
}

// ===== Audio Mute =====
function toggleMute(roomId) {
    if (roomId !== listeningRoomId || !audioElement) return;

    isMuted = !isMuted;
    audioElement.muted = isMuted;
    renderDashboard();
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
