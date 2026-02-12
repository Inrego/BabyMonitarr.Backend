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

// Initialize the audio player and connections
document.addEventListener('DOMContentLoaded', function () {
    // Create audio element for playback
    audioElement = document.createElement('audio');
    audioElement.setAttribute('autoplay', 'true');
    audioElement.setAttribute('playsinline', 'true');
    audioElement.style.display = 'none';
    document.body.appendChild(audioElement);

    // Initialize the SignalR connection
    initializeSignalRConnection();

    // Create button listeners
    setupButtonListeners();

    // Setup toggle and input auto-save listeners
    setupAutoSaveListeners();

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

    connection.start()
        .then(() => {
            console.log("SignalR Connected");
            return connection.invoke("GetAudioSettings");
        })
        .then(settings => {
            updateSettingsUI(settings);
        })
        .catch(err => {
            console.error(err);
            setTimeout(initializeSignalRConnection, 5000);
        });
}

function setupButtonListeners() {
    document.getElementById('startWebRtcStreamBtn')?.addEventListener('click', function() {
        startWebRtcStream();
    });

    document.getElementById('stopWebRtcStreamBtn')?.addEventListener('click', function() {
        stopWebRtcStream();
    });
}

function setupAutoSaveListeners() {
    // Toggle switches - save immediately on change
    const toggleIds = ['useCameraStream', 'reduceNoise', 'filterEnabled'];
    toggleIds.forEach(id => {
        document.getElementById(id)?.addEventListener('change', function() {
            saveAudioSettings();
        });
    });

    // Text/number inputs - save after a short debounce
    const inputIds = ['cameraStreamUrl', 'soundThreshold', 'highPassFrequency', 'lowPassFrequency'];
    inputIds.forEach(id => {
        document.getElementById(id)?.addEventListener('change', function() {
            debouncedSave();
        });
    });
}

function debouncedSave() {
    clearTimeout(saveDebounceTimer);
    saveDebounceTimer = setTimeout(() => {
        saveAudioSettings();
    }, 500);
}

// WebRTC Implementation
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

// UI updates
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

// Settings management
function updateSettingsUI(settings) {
    if (!settings) return;

    const highPassInput = document.getElementById('highPassFrequency');
    if (highPassInput) highPassInput.value = settings.highPassFrequency;

    const lowPassInput = document.getElementById('lowPassFrequency');
    if (lowPassInput) lowPassInput.value = settings.lowPassFrequency;

    const thresholdInput = document.getElementById('soundThreshold');
    if (thresholdInput) thresholdInput.value = settings.soundThreshold;

    // Both "Reduce Background Noise" and "Enable Audio Filters" map to filterEnabled
    const reduceNoiseInput = document.getElementById('reduceNoise');
    if (reduceNoiseInput) reduceNoiseInput.checked = settings.filterEnabled;

    const filterEnabledInput = document.getElementById('filterEnabled');
    if (filterEnabledInput) filterEnabledInput.checked = settings.filterEnabled;

    const useCameraStreamInput = document.getElementById('useCameraStream');
    if (useCameraStreamInput) useCameraStreamInput.checked = settings.useCameraAudioStream;

    const cameraUrlInput = document.getElementById('cameraStreamUrl');
    if (cameraUrlInput) cameraUrlInput.value = settings.cameraStreamUrl || '';
}

function saveAudioSettings() {
    // Determine filterEnabled from either toggle (they both control it)
    const reduceNoise = document.getElementById('reduceNoise')?.checked || false;
    const filterEnabled = document.getElementById('filterEnabled')?.checked || false;

    const settings = {
        volumeAdjustmentDb: -15.0, // Keep default since removed from UI
        highPassFrequency: parseFloat(document.getElementById('highPassFrequency')?.value || 300),
        lowPassFrequency: parseFloat(document.getElementById('lowPassFrequency')?.value || 2000),
        soundThreshold: parseFloat(document.getElementById('soundThreshold')?.value || -50),
        filterEnabled: reduceNoise || filterEnabled,
        useCameraAudioStream: document.getElementById('useCameraStream')?.checked || false,
        cameraStreamUrl: document.getElementById('cameraStreamUrl')?.value || '',
        thresholdPauseDuration: 10,
        averageSampleCount: 10
    };

    connection.invoke("UpdateAudioSettings", settings)
        .then(() => {
            showMessage("Settings saved successfully");
        })
        .catch(err => {
            console.error("Error saving settings:", err);
            showMessage("Error saving settings", true);
        });
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
