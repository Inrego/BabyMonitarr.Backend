// SignalR connection
let connection;
// WebRTC peer connection
let peerConnection;
// Audio context for processing
let audioContext;
// Audio element for playback
let audioElement;
// Queue for ICE candidates that arrive before peer connection is ready
let pendingIceCandidates = [];
// Debounce timer for auto-save
let saveDebounceTimer;

// Room state
let currentRooms = [];
let activeRoomId = null;
let selectedRoomId = null; // Room being edited in right panel
let globalSettings = {};
let selectedIcon = 'baby';

// Initialize the audio player and connections
document.addEventListener('DOMContentLoaded', function () {
    // Create audio element for playback
    audioElement = document.createElement('audio');
    audioElement.setAttribute('autoplay', 'true');
    audioElement.setAttribute('playsinline', 'true');
    audioElement.style.display = 'none';
    document.body.appendChild(audioElement);

    // Initialize the SignalR connection
    try {
        initializeSignalRConnection();
    } catch (error) {
        console.error("Error initializing SignalR connection:", error);
    }

    // Create button listeners
    setupButtonListeners();

    // Setup icon selector
    setupIconSelector();

    // Initialize audio context
    try {
        document.addEventListener('click', initAudioContext, { once: true });
    } catch (error) {
        console.error("Error initializing audio:", error);
    }
});

function initAudioContext() {
    if (!audioContext) {
        audioContext = new (window.AudioContext || window.webkitAudioContext)({
            latencyHint: 'interactive',
            sampleRate: 44100
        });
        console.log("Audio context initialized with latency mode: interactive");
    }
}

function initializeSignalRConnection() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl("/audioHub")
        .withAutomaticReconnect()
        .build();

    // Handle server ICE candidates
    connection.on("ReceiveIceCandidate", async (candidate, sdpMid, sdpMLineIndex) => {
        console.log("Received server ICE candidate:", candidate);
        const candidateStr = candidate.startsWith('candidate:') ? candidate : `candidate:${candidate}`;
        const iceCandidate = new RTCIceCandidate({
            candidate: candidateStr,
            sdpMid: sdpMid,
            sdpMLineIndex: sdpMLineIndex
        });

        if (peerConnection && peerConnection.remoteDescription) {
            try {
                await peerConnection.addIceCandidate(iceCandidate);
                console.log("Added server ICE candidate successfully");
            } catch (err) {
                console.warn("Could not add server ICE candidate:", err.message);
            }
        } else {
            console.log("Queuing ICE candidate - peer connection not ready yet");
            pendingIceCandidates.push(iceCandidate);
        }
    });

    // Handle room updates from other clients
    connection.on("RoomsUpdated", async () => {
        console.log("Rooms updated by another client");
        await loadRooms();
    });

    // Handle active room changes
    connection.on("ActiveRoomChanged", (room) => {
        console.log("Active room changed:", room);
        activeRoomId = room.id;
        renderMonitorList();
    });

    // Handle settings updates from other clients
    connection.on("SettingsUpdated", async () => {
        console.log("Settings updated by another client");
        await loadGlobalSettings();
    });

    connection.start()
        .then(async () => {
            console.log("SignalR Connected");
            await loadRooms();
            await loadGlobalSettings();

            // Handle deep-link from dashboard Configure button
            const params = new URLSearchParams(window.location.search);
            const editRoomId = params.get('editRoom');
            if (editRoomId) {
                selectMonitorForEditing(parseInt(editRoomId, 10));
            }
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
        renderMonitorList();
    } catch (err) {
        console.error("Error loading rooms:", err);
    }
}

async function loadGlobalSettings() {
    try {
        globalSettings = await connection.invoke("GetGlobalSettings");
        updateGlobalSettingsUI(globalSettings);
    } catch (err) {
        console.error("Error loading global settings:", err);
    }
}

function setupButtonListeners() {
    document.getElementById('startWebRtcStreamBtn')?.addEventListener('click', function () {
        startWebRtcStream();
    });

    document.getElementById('stopWebRtcStreamBtn')?.addEventListener('click', function () {
        stopWebRtcStream();
    });
}

function setupIconSelector() {
    document.querySelectorAll('#iconSelector .icon-option').forEach(btn => {
        btn.addEventListener('click', function () {
            document.querySelectorAll('#iconSelector .icon-option').forEach(b => b.classList.remove('selected'));
            this.classList.add('selected');
            selectedIcon = this.dataset.icon;
        });
    });
}

// ===== Monitor List Rendering =====
function renderMonitorList() {
    const container = document.getElementById('monitorList');
    if (!container) return;

    if (currentRooms.length === 0) {
        container.innerHTML = '<p style="color: var(--text-muted); text-align: center; padding: 20px;">No monitors configured</p>';
        return;
    }

    container.innerHTML = currentRooms.map(room => {
        const isActive = room.isActive;
        const isEditing = room.id === selectedRoomId;
        return `
            <div class="monitor-card ${isActive ? 'active' : ''} ${isEditing ? 'editing' : ''}" data-room-id="${room.id}">
                <div class="monitor-card-header">
                    <div class="monitor-card-icon">
                        <i class="fas fa-${room.icon || 'baby'}"></i>
                    </div>
                    <div class="monitor-card-info">
                        <div class="monitor-card-name">${escapeHtml(room.name)}</div>
                        <span class="status-badge ${isActive ? 'active' : 'offline'}">
                            <span class="status-dot"></span>
                            ${isActive ? 'Active' : 'Offline'}
                        </span>
                    </div>
                </div>
                <div class="monitor-card-actions">
                    ${!isActive ? `<button class="btn-card-action btn-activate" onclick="selectRoomForMonitoring(${room.id})"><i class="fas fa-play"></i> Activate</button>` : ''}
                    <button class="btn-card-action btn-edit" onclick="selectMonitorForEditing(${room.id})"><i class="fas fa-pen"></i> Edit</button>
                </div>
            </div>
        `;
    }).join('');

    // Update editing badge
    const editingBadge = document.getElementById('editingBadge');
    if (editingBadge) {
        editingBadge.style.display = selectedRoomId ? 'inline-block' : 'none';
    }
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// ===== Room Selection & Editing =====
function selectMonitorForEditing(id) {
    selectedRoomId = id;
    const room = currentRooms.find(r => r.id === id);
    if (!room) return;

    // Update breadcrumb and title
    const breadcrumb = document.getElementById('breadcrumbRoomName');
    const title = document.getElementById('pageTitleRoomName');
    if (breadcrumb) breadcrumb.textContent = room.name;
    if (title) title.textContent = room.name;

    // Show config panel, hide placeholder
    document.getElementById('noRoomPlaceholder').style.display = 'none';
    document.getElementById('roomConfigPanel').style.display = 'block';

    // Populate room fields
    const nameInput = document.getElementById('roomName');
    if (nameInput) nameInput.value = room.name;

    const typeSelect = document.getElementById('monitorType');
    if (typeSelect) typeSelect.value = room.monitorType || 'camera_audio';

    // Set icon
    selectedIcon = room.icon || 'baby';
    document.querySelectorAll('#iconSelector .icon-option').forEach(btn => {
        btn.classList.toggle('selected', btn.dataset.icon === selectedIcon);
    });

    // Source config
    const enableVideo = document.getElementById('enableVideoStream');
    if (enableVideo) enableVideo.checked = room.enableVideoStream;

    const cameraUrl = document.getElementById('cameraStreamUrl');
    if (cameraUrl) cameraUrl.value = room.cameraStreamUrl || '';

    const useCameraAudio = document.getElementById('useCameraStream');
    if (useCameraAudio) useCameraAudio.checked = room.useCameraAudioStream;

    const fallbackDevice = document.getElementById('fallbackAudioDevice');
    if (fallbackDevice) fallbackDevice.value = room.fallbackAudioDevice || '';

    // Re-render list to show editing state
    renderMonitorList();
}

async function selectRoomForMonitoring(id) {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        showMessage("Not connected to server.", true);
        return;
    }

    try {
        const room = await connection.invoke("SelectRoom", id);
        if (room) {
            activeRoomId = room.id;
            renderMonitorList();
            showMessage(`Now monitoring: ${room.name}`);
        }
    } catch (err) {
        console.error("Error selecting room:", err);
        showMessage("Error activating room", true);
    }
}

// ===== Room CRUD =====
async function addNewMonitor() {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        showMessage("Not connected to server.", true);
        return;
    }

    try {
        const room = await connection.invoke("CreateRoom", {
            name: "New Monitor",
            icon: "baby",
            monitorType: "camera_audio",
            enableVideoStream: false,
            cameraStreamUrl: "",
            cameraUsername: "",
            cameraPassword: "",
            useCameraAudioStream: false,
            fallbackAudioDevice: "",
            isActive: false
        });

        await loadRooms();
        selectMonitorForEditing(room.id);
        showMessage("Monitor created");
    } catch (err) {
        console.error("Error creating room:", err);
        showMessage("Error creating monitor", true);
    }
}

async function saveRoomConfig() {
    if (!selectedRoomId) return;
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        showMessage("Not connected to server.", true);
        return;
    }

    const room = currentRooms.find(r => r.id === selectedRoomId);
    if (!room) return;

    // Gather room-specific fields
    const updatedRoom = {
        id: selectedRoomId,
        name: document.getElementById('roomName')?.value || 'Unnamed',
        icon: selectedIcon,
        monitorType: document.getElementById('monitorType')?.value || 'camera_audio',
        enableVideoStream: document.getElementById('enableVideoStream')?.checked || false,
        cameraStreamUrl: document.getElementById('cameraStreamUrl')?.value || '',
        cameraUsername: room.cameraUsername || '',
        cameraPassword: room.cameraPassword || '',
        useCameraAudioStream: document.getElementById('useCameraStream')?.checked || false,
        fallbackAudioDevice: document.getElementById('fallbackAudioDevice')?.value || '',
        isActive: room.isActive
    };

    // Gather global audio processing settings
    const reduceNoise = document.getElementById('reduceNoise')?.checked || false;
    const filterEnabled = document.getElementById('filterEnabled')?.checked || false;

    const audioSettings = {
        soundThreshold: parseFloat(document.getElementById('soundThreshold')?.value || -20),
        averageSampleCount: 10,
        filterEnabled: reduceNoise || filterEnabled,
        lowPassFrequency: parseInt(document.getElementById('lowPassFrequency')?.value || 4000),
        highPassFrequency: parseInt(document.getElementById('highPassFrequency')?.value || 300),
        thresholdPauseDuration: 30,
        volumeAdjustmentDb: -15.0
    };

    try {
        // Save room and global settings in parallel
        const [updatedRoomResult] = await Promise.all([
            connection.invoke("UpdateRoom", updatedRoom),
            connection.invoke("UpdateAudioSettings", audioSettings)
        ]);

        if (updatedRoomResult) {
            await loadRooms();
            // Re-select to refresh UI
            selectMonitorForEditing(selectedRoomId);
            showMessage("Configuration saved");
        } else {
            showMessage("Error: room not found", true);
        }
    } catch (err) {
        console.error("Error saving configuration:", err);
        showMessage("Error saving configuration", true);
    }
}

async function deleteCurrentRoom() {
    if (!selectedRoomId) return;
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        showMessage("Not connected to server.", true);
        return;
    }

    const room = currentRooms.find(r => r.id === selectedRoomId);
    if (!room) return;

    if (!confirm(`Delete monitor "${room.name}"? This cannot be undone.`)) return;

    try {
        const result = await connection.invoke("DeleteRoom", selectedRoomId);
        if (result) {
            selectedRoomId = null;
            document.getElementById('noRoomPlaceholder').style.display = 'block';
            document.getElementById('roomConfigPanel').style.display = 'none';
            document.getElementById('breadcrumbRoomName').textContent = 'Select a Monitor';
            document.getElementById('pageTitleRoomName').textContent = 'Monitor';
            await loadRooms();
            showMessage("Monitor deleted");
        }
    } catch (err) {
        console.error("Error deleting room:", err);
        showMessage("Error deleting monitor", true);
    }
}

// ===== Global Settings UI =====
function updateGlobalSettingsUI(settings) {
    if (!settings) return;

    const thresholdInput = document.getElementById('soundThreshold');
    if (thresholdInput) thresholdInput.value = settings.soundThreshold;

    const reduceNoiseInput = document.getElementById('reduceNoise');
    if (reduceNoiseInput) reduceNoiseInput.checked = settings.filterEnabled;

    const filterEnabledInput = document.getElementById('filterEnabled');
    if (filterEnabledInput) filterEnabledInput.checked = settings.filterEnabled;

    const highPassInput = document.getElementById('highPassFrequency');
    if (highPassInput) highPassInput.value = settings.highPassFrequency;

    const lowPassInput = document.getElementById('lowPassFrequency');
    if (lowPassInput) lowPassInput.value = settings.lowPassFrequency;
}

// ===== WebRTC Implementation =====
async function startWebRtcStream() {
    try {
        if (peerConnection) {
            await stopWebRtcStream();
        }

        console.log("Starting WebRTC stream...");

        console.log("Calling StartWebRtcStream on hub...");
        const offerSdp = await connection.invoke("StartWebRtcStream");
        console.log("Got offer from server");

        const configuration = {
            iceServers: [
                { urls: 'stun:stun.l.google.com:19302' }
            ]
        };

        peerConnection = new RTCPeerConnection(configuration);

        setupPeerConnectionHandlers();

        await peerConnection.setRemoteDescription({ type: 'offer', sdp: offerSdp });

        const answer = await peerConnection.createAnswer();
        await peerConnection.setLocalDescription(answer);

        await connection.invoke("SetRemoteDescription", answer.type, answer.sdp);

        if (pendingIceCandidates.length > 0) {
            console.log(`Processing ${pendingIceCandidates.length} queued ICE candidates`);
            for (const candidate of pendingIceCandidates) {
                try {
                    await peerConnection.addIceCandidate(candidate);
                    console.log("Added queued ICE candidate successfully");
                } catch (err) {
                    console.warn("Could not add queued ICE candidate:", err.message);
                }
            }
            pendingIceCandidates = [];
        }

        // Update monitoring badge
        setMonitoringState(true);
        console.log("WebRTC stream negotiation started");
    } catch (error) {
        console.error("Error starting WebRTC stream:", error);
    }
}

function setupPeerConnectionHandlers() {
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
        console.log("Data channel received:", event.channel.label);
        const dataChannel = event.channel;

        dataChannel.onopen = () => {
            console.log("Data channel opened:", dataChannel.label);
        };

        dataChannel.onmessage = (event) => {
            try {
                const message = JSON.parse(event.data);
                if (message.type === 'audioLevel') {
                    updateAudioMeter(message.level);
                } else if (message.type === 'soundAlert') {
                    showSoundAlert(message.level, message.threshold);
                }
            } catch (err) {
                console.error("Error parsing data channel message:", err);
            }
        };

        dataChannel.onclose = () => {
            console.log("Data channel closed:", dataChannel.label);
        };

        dataChannel.onerror = (error) => {
            console.error("Data channel error:", error);
        };
    };

    peerConnection.onconnectionstatechange = () => {
        console.log("WebRTC connection state:", peerConnection.connectionState);

        if (peerConnection.connectionState === 'connected') {
            console.log("WebRTC connected. Audio should be streaming via track.");
            setMonitoringState(true);
        }

        if (peerConnection.connectionState === 'disconnected' ||
            peerConnection.connectionState === 'failed' ||
            peerConnection.connectionState === 'closed') {
            if (audioElement) {
                audioElement.srcObject = null;
            }
            setMonitoringState(false);
        }
    };

    peerConnection.ontrack = (event) => {
        console.log("Remote track received:", event.track, "Streams:", event.streams);
        if (event.streams && event.streams[0] && audioElement) {
            audioElement.srcObject = event.streams[0];
            audioElement.play().catch(e => console.error("Error playing WebRTC audio track:", e));
            console.log("Assigned remote stream to audio element.");
        } else {
            console.warn("Received track, but no stream or audio element available.");
        }
    };
}

async function stopWebRtcStream() {
    pendingIceCandidates = [];

    if (peerConnection) {
        try {
            await connection.invoke("StopWebRtcStream");

            peerConnection.close();
            peerConnection = null;

            if (audioElement) {
                audioElement.srcObject = null;
            }

            setMonitoringState(false);
            console.log("WebRTC stream stopped");
        } catch (error) {
            console.error("Error stopping WebRTC stream:", error);
        }
    }
}

// ===== UI Updates =====
function setMonitoringState(isMonitoring) {
    const badge = document.getElementById('monitoringBadge');
    const badgeText = document.getElementById('monitoringBadgeText');
    if (!badge || !badgeText) return;

    if (isMonitoring) {
        badge.classList.remove('inactive');
        badgeText.textContent = 'Currently Monitoring';
    } else {
        badge.classList.add('inactive');
        badgeText.textContent = 'Not Monitoring';
    }
}

function updateAudioMeter(level) {
    const meter = document.getElementById('audioMeter');
    if (!meter) return;

    // Map the dB level to a meter percentage (-90 to 0)
    const minDb = -90;
    const percentage = 100 - Math.max(0, Math.min(100, ((level - 0) / minDb) * 100));

    meter.style.width = percentage + '%';

    // Update large number display
    const levelValue = document.getElementById('audioLevelValue');
    if (levelValue) {
        levelValue.textContent = Math.abs(level).toFixed(1);
    }

    // Update subtitle text
    const levelText = document.getElementById('audioLevelText');
    if (levelText) {
        levelText.textContent = level.toFixed(1);
    }
}

function showSoundAlert(level, threshold) {
    const alertElement = document.getElementById('soundAlert');
    if (!alertElement) return;

    alertElement.textContent = `Sound detected: ${level.toFixed(1)} dB (threshold: ${threshold.toFixed(1)} dB)`;
    alertElement.style.display = 'block';

    setTimeout(() => {
        alertElement.style.display = 'none';
    }, 5000);
}

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
