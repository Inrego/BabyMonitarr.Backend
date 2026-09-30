# Nest camera talkback (push-to-talk)

A parent presses and holds **Talk** in the app and their voice plays out of the room's Nest camera
speaker. Monitoring is untouched: it stays on the official SDM stream. Talkback runs on a second,
on-demand stream through Google Home's private "Foyer" API, which needs a separate Google Home
credential (see [GOOGLE_HOME_TALKBACK_SETUP.md](GOOGLE_HOME_TALKBACK_SETUP.md)). When that credential
is missing or failing, only talkback is disabled.

```
app mic ──WebRTC (talkback uplink, Opus)──▶ backend ──gain/limiter──▶ Foyer WebRTC stream ──▶ camera speaker
```

## SignalR contract (`/audioHub`)

All payloads use SignalR's default camelCase JSON. Room ids are the backend room ids.

### Capability and status

`GetTalkbackStatus(int roomId) → TalkbackStatus`

Pushed to every client whenever any field changes: `TalkbackStatusChanged(TalkbackStatus status)`.

```jsonc
{
  "roomId": 3,
  "supported": true,          // room is a Nest room: show the Talk UI at all
  "available": true,          // talking can be started now (credential OK, camera mapped)
  "unavailableReason": null,  // null | "not_nest" | "not_configured" | "credential_failing"
                              //      | "camera_not_mapped" | "camera_error"
  "message": null,            // human-readable detail for the reason, or for the last error
  "state": "closed",          // "closed" | "connecting" | "open" | "talking"
  "busy": false,              // someone (any client) is talking in this room right now
  "volume": 1.0               // talkback gain 0.0–2.0, 1.0 = unchanged; persisted per room
}
```

`available == false` with `supported == true` means: show the Talk button disabled, with `message`.
`camera_error` is transient (the last Foyer attempt failed): `available` stays `true`, `message` says
what failed, and the next `StartTalkback` retries. Every other non-null reason comes with
`available == false`.

Measured against a real camera: opening the Foyer stream takes ~0.8 s warm and 1.4–1.7 s cold, plus
~0.3 s for the speaker to start, so a press goes live in 1–2 s without any warm-up.

### Microphone uplink (a separate peer connection per room)

The monitoring peer connection (`StartAudioStream`) is not changed. The mic goes over its own peer
connection, server-offered like the others:

| Direction | Call | Notes |
|---|---|---|
| app → server | `StartTalkbackUplink(int roomId) → string offerSdp` | One audio m-line, Opus/48000/2 (PT 111), `a=recvonly`. No data channel. Replaces an existing uplink of this connection for the room. |
| app → server | `SetTalkbackRemoteDescription(int roomId, string type, string sdp)` | `type` = `"answer"`. |
| app → server | `AddTalkbackIceCandidate(int roomId, string candidate, string sdpMid, int? sdpMLineIndex)` | |
| server → app | `ReceiveTalkbackIceCandidate(int roomId, string candidate, string sdpMid, int sdpMLineIndex)` | Same shape as `ReceiveAudioIceCandidate`. |
| app → server | `StopTalkbackUplink(int roomId)` | Idempotent. Also stops talking if this connection was talking. |

App side: `setRemoteDescription(offer)`, then `getUserMedia({audio: {echoCancellation: true,
noiseSuppression: true, autoGainControl: true}})`, `addTrack(micTrack, stream)`, `createAnswer()`. The
answer's audio section becomes `sendonly`. Use the same ICE servers as monitoring (`GetWebRtcConfig`).

### Talking

| Call | Behaviour |
|---|---|
| `StartTalkback(int roomId) → TalkbackStartResult` | Opens the Foyer stream if needed (typically 1–3 s), then starts the camera speaker. Returns when the speaker is live. Never throws for an expected failure; bounded to 9 s. Does not need the uplink to be connected first. Idempotent for the connection that is already talking. |
| `StopTalkback(int roomId)` | Idempotent. The Foyer stream stays open for a short idle period so a quick re-press is fast. Arriving while this connection's `StartTalkback` is still opening, it cancels that start (which then returns `reason: "cancelled"`), so the speaker never goes live after release. |
| `PrepareTalkback(int roomId)` | Optional warm-up: opens the Foyer stream without talking. It closes again after the idle period. |
| `SetTalkbackVolume(int roomId, double volume)` | Clamped to 0.0–2.0, persisted, broadcast via `TalkbackStatusChanged`. Applies immediately, also mid-talk. |

```jsonc
// TalkbackStartResult
{ "success": false, "reason": "busy", "message": "Someone else is talking in this room" }
// reason: null on success | "busy" | "cancelled" | one of the unavailableReason values
```

Audio that arrives on the uplink is forwarded only while *this* connection is the room's talker;
anything before `StartTalkback` succeeds is dropped. One talker per room.

The server stops talking on its own when the talker's SignalR connection drops, its uplink closes,
no uplink audio has arrived for 10 s while talking (stuck button, dead app), or after 5 minutes of
continuous talking. Each of these is broadcast as a `TalkbackStatusChanged` with `state` leaving
`"talking"`.

### Suggested app sequence

1. On the monitor screen, `GetTalkbackStatus(roomId)`; subscribe to `TalkbackStatusChanged`.
2. Press: in parallel, negotiate the uplink (`StartTalkbackUplink` …) and call `StartTalkback`. Show
   "connecting" until both are done, then "talking". Duck/mute the room's incoming audio.
3. Release: `StopTalkback`, then `StopTalkbackUplink` (releases the mic). Restore room audio.

## Backend configuration

- Google Home credential: System page → *Google Home (talkback)*. Stored server-side, never returned
  to clients or logged.
- Camera mapping: resolved automatically from the Google Home graph (same room name, or the only
  camera); when ambiguous, pick it in the room's config (*Talkback camera*).
