// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

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
    
    // Initialize audio context
    try {
        // AudioContext must be created after user interaction
        document.addEventListener('click', initAudioContext, { once: true });
    } catch (error) {
        console.error("Error initializing audio:", error);
    }
});

function initAudioContext() {
    if (!audioContext) {
        audioContext = new (window.AudioContext || window.webkitAudioContext)({
            latencyHint: 'interactive',  // Optimize for low latency
            sampleRate: 44100           // Use standard sample rate
        });
        console.log("Audio context initialized with latency mode: interactive");
    }
}

function initializeSignalRConnection() {
    // Create the SignalR connection
    connection = new signalR.HubConnectionBuilder()
        .withUrl("/audioHub")
        .withAutomaticReconnect()
        .build();

    // Handle server ICE candidates
    connection.on("ReceiveIceCandidate", async (candidate, sdpMid, sdpMLineIndex) => {
        console.log("Received server ICE candidate:", candidate);
        // Ensure the candidate string has the proper format (must start with "candidate:")
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
                // Log but don't fail - connection may still succeed via other candidates
                console.warn("Could not add server ICE candidate:", err.message);
            }
        } else {
            // Queue the candidate for later
            console.log("Queuing ICE candidate - peer connection not ready yet");
            pendingIceCandidates.push(iceCandidate);
        }
    });

    // Start the connection
    connection.start()
        .then(() => {
            console.log("SignalR Connected");
            // Get initial audio settings
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
    // Start WebRTC streaming button
    document.getElementById('startWebRtcStreamBtn')?.addEventListener('click', function() {
        startWebRtcStream();
    });

    // Stop WebRTC streaming button
    document.getElementById('stopWebRtcStreamBtn')?.addEventListener('click', function() {
        stopWebRtcStream();
    });

    // Save settings button
    document.getElementById('saveSettingsBtn')?.addEventListener('click', function() {
        saveAudioSettings();
    });
}

// WebRTC Implementation with Media Stream for audio streaming
async function startWebRtcStream() {
    try {
        // Close any existing peer connection
        if (peerConnection) {
            await stopWebRtcStream();
        }

        console.log("Starting WebRTC stream...");
        // initAudioContext(); // AudioContext not strictly needed for direct MediaStream playback

        // Get the offer SDP from the server
        // The Hub method should be updated if its responsibilities change, but for now, assume it's just an offer
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

        // Process any ICE candidates that arrived before negotiation completed
        if (pendingIceCandidates.length > 0) {
            console.log(`Processing ${pendingIceCandidates.length} queued ICE candidates`);
            for (const candidate of pendingIceCandidates) {
                try {
                    await peerConnection.addIceCandidate(candidate);
                    console.log("Added queued ICE candidate successfully");
                } catch (err) {
                    // Log but don't fail - connection may still succeed via other candidates
                    console.warn("Could not add queued ICE candidate:", err.message);
                }
            }
            pendingIceCandidates = [];
        }

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

    // Handle incoming data channel for audio level updates
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

    // Handle connection state changes
    peerConnection.onconnectionstatechange = () => {
        console.log("WebRTC connection state:", peerConnection.connectionState);

        if (peerConnection.connectionState === 'connected') {
            console.log("WebRTC connected. Audio should be streaming via track.");
        }

        if (peerConnection.connectionState === 'disconnected' ||
            peerConnection.connectionState === 'failed' ||
            peerConnection.connectionState === 'closed') {
            if (audioElement) {
                audioElement.srcObject = null; // Clear the stream from audio element
            }
        }
    };

    // Handle incoming remote tracks
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
    // Clear any pending ICE candidates
    pendingIceCandidates = [];

    if (peerConnection) {
        try {
            await connection.invoke("StopWebRtcStream");

            peerConnection.close();
            peerConnection = null;

            if (audioElement) {
                audioElement.srcObject = null;
            }
            console.log("WebRTC stream stopped");
        } catch (error) {
            console.error("Error stopping WebRTC stream:", error);
        }
    }
}

// UI updates
function updateAudioMeter(level) {
    const meter = document.getElementById('audioMeter');
    if (!meter) return;
    
    // Map the dB level to a meter percentage
    // Assuming level is in dB between -90 and 0
    const minDb = -90;
    const percentage = 100 - Math.max(0, Math.min(100, ((level - 0) / minDb) * 100));
    
    meter.style.width = percentage + '%';
    
    // Update color based on level
    if (level > -20) {
        meter.style.backgroundColor = '#ff6347'; // High (red)
    } else if (level > -40) {
        meter.style.backgroundColor = '#ffa500'; // Medium (orange)
    } else {
        meter.style.backgroundColor = '#4caf50'; // Low (green)
    }
    
    // Update level text if present
    const levelText = document.getElementById('audioLevelText');
    if (levelText) {
        levelText.textContent = level.toFixed(1) + ' dB';
    }
}

function showSoundAlert(level, threshold) {
    const alertElement = document.getElementById('soundAlert');
    if (!alertElement) return;
    
    // Show the alert with level and threshold info
    alertElement.textContent = `Sound detected: ${level.toFixed(1)} dB (threshold: ${threshold.toFixed(1)} dB)`;
    alertElement.style.display = 'block';
    
    // Hide after 5 seconds
    setTimeout(() => {
        alertElement.style.display = 'none';
    }, 5000);
}

// Settings management
function updateSettingsUI(settings) {
    if (!settings) return;
    
    // Update each setting input
    const volumeInput = document.getElementById('volumeAdjustment');
    if (volumeInput) volumeInput.value = settings.volumeAdjustmentDb;
    
    const highPassInput = document.getElementById('highPassFrequency');
    if (highPassInput) highPassInput.value = settings.highPassFrequency;
    
    const lowPassInput = document.getElementById('lowPassFrequency');
    if (lowPassInput) lowPassInput.value = settings.lowPassFrequency;
    
    const thresholdInput = document.getElementById('soundThreshold');
    if (thresholdInput) thresholdInput.value = settings.soundThreshold;
    
    const filterEnabledInput = document.getElementById('filterEnabled');
    if (filterEnabledInput) filterEnabledInput.checked = settings.filterEnabled;
    
    const useCameraStreamInput = document.getElementById('useCameraStream');
    if (useCameraStreamInput) useCameraStreamInput.checked = settings.useCameraAudioStream;
    
    const cameraUrlInput = document.getElementById('cameraStreamUrl');
    if (cameraUrlInput) cameraUrlInput.value = settings.cameraStreamUrl;
}

function saveAudioSettings() {
    // Collect settings from form inputs
    const settings = {
        volumeAdjustmentDb: parseFloat(document.getElementById('volumeAdjustment')?.value || 0),
        highPassFrequency: parseFloat(document.getElementById('highPassFrequency')?.value || 300),
        lowPassFrequency: parseFloat(document.getElementById('lowPassFrequency')?.value || 2000),
        soundThreshold: parseFloat(document.getElementById('soundThreshold')?.value || -50),
        filterEnabled: document.getElementById('filterEnabled')?.checked || false,
        useCameraAudioStream: document.getElementById('useCameraStream')?.checked || false,
        cameraStreamUrl: document.getElementById('cameraStreamUrl')?.value || '',
        thresholdPauseDuration: 10, // Default or get from UI if you have an input
        averageSampleCount: 10 // Default or get from UI if you have an input
    };
    
    // Send settings to the server
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
    messageElement.className = isError ? 'error-message' : 'success-message';
    messageElement.style.display = 'block';
    
    // Hide after 3 seconds
    setTimeout(() => {
        messageElement.style.display = 'none';
    }, 3000);
}
