# BabyMonitarr

A baby monitor audio streaming application that captures audio from a local microphone or IP camera and streams it to connected clients via WebRTC.

## Architecture Overview

The application uses:
- **ASP.NET Core** backend with SignalR for WebRTC signaling
- **WebRTC** for low-latency audio streaming
- **WebRTC Data Channel** for real-time audio level updates and sound alerts

### Why WebRTC?

WebRTC provides:
- Low-latency peer-to-peer audio streaming
- Opus codec for efficient audio compression
- Built-in NAT traversal via ICE candidates
- Data channels for lightweight metadata transmission

## Client Integration Guide

To build a client that connects to BabyMonitarr, follow this protocol:

### 1. SignalR Connection

Connect to the SignalR hub at `/audioHub`:

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/audioHub")
    .withAutomaticReconnect()
    .build();

await connection.start();
```

### 2. WebRTC Connection Flow

#### Step 1: Request Stream (Client invokes)

```javascript
// Returns SDP offer string
const offerSdp = await connection.invoke("StartWebRtcStream");
```

#### Step 2: Create RTCPeerConnection

```javascript
const configuration = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' }
    ]
};

const peerConnection = new RTCPeerConnection(configuration);
```

#### Step 3: Set Remote Description and Create Answer

```javascript
await peerConnection.setRemoteDescription({ type: 'offer', sdp: offerSdp });
const answer = await peerConnection.createAnswer();
await peerConnection.setLocalDescription(answer);

// Send answer to server
await connection.invoke("SetRemoteDescription", answer.type, answer.sdp);
```

#### Step 4: Handle ICE Candidates

The server sends ICE candidates via SignalR. Queue them if they arrive before the peer connection is ready:

```javascript
let pendingIceCandidates = [];

connection.on("ReceiveIceCandidate", async (candidate, sdpMid, sdpMLineIndex) => {
    // Server may send candidates without "candidate:" prefix
    const candidateStr = candidate.startsWith('candidate:') ? candidate : `candidate:${candidate}`;
    const iceCandidate = new RTCIceCandidate({
        candidate: candidateStr,
        sdpMid: sdpMid,
        sdpMLineIndex: sdpMLineIndex
    });

    if (peerConnection && peerConnection.remoteDescription) {
        await peerConnection.addIceCandidate(iceCandidate);
    } else {
        pendingIceCandidates.push(iceCandidate);
    }
});
```

Send client ICE candidates to the server:

```javascript
peerConnection.onicecandidate = async (event) => {
    if (event.candidate) {
        await connection.invoke("AddIceCandidate",
            event.candidate.candidate,
            event.candidate.sdpMid,
            event.candidate.sdpMLineIndex
        );
    }
};
```

#### Step 5: Receive Audio Track

```javascript
peerConnection.ontrack = (event) => {
    if (event.streams && event.streams[0]) {
        const audioElement = document.createElement('audio');
        audioElement.srcObject = event.streams[0];
        audioElement.play();
    }
};
```

#### Step 6: Handle Data Channel

The server creates a data channel named `audioLevels` for sending audio level updates and sound alerts:

```javascript
peerConnection.ondatachannel = (event) => {
    const dataChannel = event.channel;

    dataChannel.onmessage = (event) => {
        const message = JSON.parse(event.data);

        if (message.type === 'audioLevel') {
            // message.level: audio level in dB (typically -90 to 0)
            // message.timestamp: Unix timestamp in milliseconds
            console.log(`Audio level: ${message.level} dB`);
        } else if (message.type === 'soundAlert') {
            // message.level: current audio level in dB
            // message.threshold: threshold that was exceeded
            // message.timestamp: Unix timestamp in milliseconds
            console.log(`Sound alert: ${message.level} dB exceeded threshold ${message.threshold} dB`);
        }
    };
};
```

#### Step 7: Stop Stream

```javascript
await connection.invoke("StopWebRtcStream");
peerConnection.close();
```

### 3. Data Channel Message Formats

#### Audio Level Update
```json
{
    "type": "audioLevel",
    "level": -45.2,
    "timestamp": 1702742400000
}
```

| Field | Type | Description |
|-------|------|-------------|
| `type` | string | Always `"audioLevel"` |
| `level` | number | Audio level in decibels (dB), typically -90 to 0 |
| `timestamp` | number | Unix timestamp in milliseconds |

#### Sound Alert
```json
{
    "type": "soundAlert",
    "level": -15.3,
    "threshold": -20.0,
    "timestamp": 1702742400000
}
```

| Field | Type | Description |
|-------|------|-------------|
| `type` | string | Always `"soundAlert"` |
| `level` | number | Current audio level in dB that triggered the alert |
| `threshold` | number | The threshold setting that was exceeded |
| `timestamp` | number | Unix timestamp in milliseconds |

### 4. Audio Settings Management

#### Get Current Settings

```javascript
const settings = await connection.invoke("GetAudioSettings");
```

Returns an `AudioSettings` object:

```json
{
    "soundThreshold": -20.0,
    "averageSampleCount": 10,
    "filterEnabled": false,
    "lowPassFrequency": 4000,
    "highPassFrequency": 300,
    "cameraStreamUrl": null,
    "useCameraAudioStream": false,
    "thresholdPauseDuration": 30,
    "volumeAdjustmentDb": -15.0
}
```

#### Update Settings

```javascript
await connection.invoke("UpdateAudioSettings", {
    soundThreshold: -25.0,
    averageSampleCount: 10,
    filterEnabled: true,
    lowPassFrequency: 4000,
    highPassFrequency: 300,
    cameraStreamUrl: "rtsp://192.168.1.100:554/stream",
    useCameraAudioStream: true,
    thresholdPauseDuration: 30,
    volumeAdjustmentDb: -10.0
});
```

### Audio Settings Reference

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `soundThreshold` | number | -20.0 | Sound threshold in dB that triggers alerts |
| `averageSampleCount` | number | 10 | Number of samples for average calculation |
| `filterEnabled` | boolean | false | Enable audio filtering |
| `lowPassFrequency` | number | 4000 | Low-pass filter cutoff frequency (Hz) |
| `highPassFrequency` | number | 300 | High-pass filter cutoff frequency (Hz) |
| `cameraStreamUrl` | string | null | RTSP/HTTP URL for IP camera audio |
| `useCameraAudioStream` | boolean | false | Use camera audio instead of local microphone |
| `thresholdPauseDuration` | number | 30 | Seconds to pause alerts after threshold exceeded |
| `volumeAdjustmentDb` | number | -15.0 | Volume adjustment in dB (-20 to 20) |

## SignalR Hub Methods Summary

### Client-to-Server (Invoke)

| Method | Parameters | Returns | Description |
|--------|------------|---------|-------------|
| `StartWebRtcStream` | none | `string` (SDP offer) | Start WebRTC stream, returns SDP offer |
| `SetRemoteDescription` | `type: string`, `sdp: string` | void | Set the client's SDP answer |
| `AddIceCandidate` | `candidate: string`, `sdpMid: string`, `sdpMLineIndex: int?` | void | Add client ICE candidate |
| `StopWebRtcStream` | none | void | Stop the WebRTC stream |
| `GetAudioSettings` | none | `AudioSettings` | Get current audio settings |
| `UpdateAudioSettings` | `settings: AudioSettings` | void | Update audio settings |

### Server-to-Client (On)

| Event | Parameters | Description |
|-------|------------|-------------|
| `ReceiveIceCandidate` | `candidate: string`, `sdpMid: string`, `sdpMLineIndex: int` | Server ICE candidate |

## Audio Format

- **Codec**: Opus (negotiated via WebRTC)
- **Sample Rate**: 48kHz (resampled from 44.1kHz source)
- **Channels**: Mono

## Complete Example

```javascript
class BabyMonitorClient {
    constructor(hubUrl) {
        this.hubUrl = hubUrl;
        this.connection = null;
        this.peerConnection = null;
        this.pendingIceCandidates = [];
        this.audioElement = null;
    }

    async connect() {
        // Create SignalR connection
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(this.hubUrl)
            .withAutomaticReconnect()
            .build();

        // Handle ICE candidates from server
        this.connection.on("ReceiveIceCandidate", async (candidate, sdpMid, sdpMLineIndex) => {
            const candidateStr = candidate.startsWith('candidate:') ? candidate : `candidate:${candidate}`;
            const iceCandidate = new RTCIceCandidate({
                candidate: candidateStr,
                sdpMid: sdpMid,
                sdpMLineIndex: sdpMLineIndex
            });

            if (this.peerConnection && this.peerConnection.remoteDescription) {
                try {
                    await this.peerConnection.addIceCandidate(iceCandidate);
                } catch (err) {
                    console.warn("Could not add ICE candidate:", err.message);
                }
            } else {
                this.pendingIceCandidates.push(iceCandidate);
            }
        });

        await this.connection.start();
        console.log("SignalR connected");
    }

    async startStream() {
        // Get offer from server
        const offerSdp = await this.connection.invoke("StartWebRtcStream");

        // Create peer connection
        this.peerConnection = new RTCPeerConnection({
            iceServers: [{ urls: 'stun:stun.l.google.com:19302' }]
        });

        // Handle ICE candidates
        this.peerConnection.onicecandidate = async (event) => {
            if (event.candidate) {
                await this.connection.invoke("AddIceCandidate",
                    event.candidate.candidate,
                    event.candidate.sdpMid,
                    event.candidate.sdpMLineIndex
                );
            }
        };

        // Handle audio track
        this.peerConnection.ontrack = (event) => {
            if (event.streams && event.streams[0]) {
                this.audioElement = new Audio();
                this.audioElement.srcObject = event.streams[0];
                this.audioElement.play();
            }
        };

        // Handle data channel
        this.peerConnection.ondatachannel = (event) => {
            event.channel.onmessage = (e) => {
                const message = JSON.parse(e.data);
                if (message.type === 'audioLevel') {
                    this.onAudioLevel(message.level, message.timestamp);
                } else if (message.type === 'soundAlert') {
                    this.onSoundAlert(message.level, message.threshold, message.timestamp);
                }
            };
        };

        // Set remote description
        await this.peerConnection.setRemoteDescription({ type: 'offer', sdp: offerSdp });

        // Create and send answer
        const answer = await this.peerConnection.createAnswer();
        await this.peerConnection.setLocalDescription(answer);
        await this.connection.invoke("SetRemoteDescription", answer.type, answer.sdp);

        // Process queued ICE candidates
        for (const candidate of this.pendingIceCandidates) {
            try {
                await this.peerConnection.addIceCandidate(candidate);
            } catch (err) {
                console.warn("Could not add queued ICE candidate:", err.message);
            }
        }
        this.pendingIceCandidates = [];
    }

    async stopStream() {
        this.pendingIceCandidates = [];
        if (this.peerConnection) {
            await this.connection.invoke("StopWebRtcStream");
            this.peerConnection.close();
            this.peerConnection = null;
        }
        if (this.audioElement) {
            this.audioElement.srcObject = null;
            this.audioElement = null;
        }
    }

    // Override these methods to handle events
    onAudioLevel(level, timestamp) {
        console.log(`Audio level: ${level} dB`);
    }

    onSoundAlert(level, threshold, timestamp) {
        console.log(`Sound alert: ${level} dB exceeded ${threshold} dB`);
    }
}

// Usage
const client = new BabyMonitorClient("/audioHub");
await client.connect();
await client.startStream();
```

## License

MIT
