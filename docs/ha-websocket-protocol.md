# BabyMonitarr ↔ Home Assistant WebSocket protocol

Version **1**. Implemented in `Ha/` (`HaWebSocketEndpoint.cs`, `HaConnection.cs`,
`HaMonitoringService.cs`, `HaWebRtcBridge.cs`, `HaCastBridge.cs`, `HaPeerRegistry.cs`,
`HaMessages.cs`, `HaJson.cs`). This document is the contract; if code and document disagree, that
is a bug in one of them.

The endpoint is plain WebSocket, deliberately not SignalR — the Home Assistant side has no
SignalR client. It runs alongside the existing `/audioHub` SignalR hub and changes nothing about
it.

---

## 1. Connecting

```
GET /ha/ws
Upgrade: websocket
Authorization: Bearer <babymonitarr-api-key>
```

or, when the client cannot set headers on the handshake (HA's `aiohttp` cannot always):

```
GET /ha/ws?access_token=<babymonitarr-api-key>
```

Auth rules:

- The **header is preferred**. `access_token` is checked next, then `api_key`. The first one that
  yields a non-empty value is the one validated; the others are ignored.
- The key is a BabyMonitarr API key, validated against `IApiKeyService.ValidateKeyAsync` — the
  same keys the mobile app uses, created in the BabyMonitarr UI.
- A missing or invalid key is answered with **HTTP 401 before the upgrade**, body `Missing API key`
  or `Invalid API key`, plus `WWW-Authenticate: Bearer`. The socket is never accepted and then
  closed for auth reasons, so a client can distinguish "bad credentials" from "server went away".
- A non-WebSocket `GET /ha/ws` is answered with HTTP 400.

No Home Assistant token is stored on the BabyMonitarr side. Trust flows one way.

**TLS.** If the deployment terminates TLS, use `wss://`. The app applies HTTPS redirection to all
paths except `/cast/hls`, so an unencrypted `ws://` handshake against an HTTPS-configured instance
may be redirected rather than upgraded. On a plain-HTTP LAN deployment (the common case) `ws://`
works.

---

## 2. Envelope

Every frame in both directions is a single JSON text message:

```jsonc
{
  "v": 1,                          // int, protocol version
  "type": "sound_state",           // string, message type (see below)
  "ts": "2026-09-22T12:34:56.789Z", // string, UTC ISO-8601, when the server built the frame
  "ref": "42",                     // string|null, present only on a reply; echoes the request's "id"
  "data": { }                      // object|null, type-specific payload
}
```

- `ts` is server time, UTC. Clients should not rely on it for ordering beyond "later frames have
  later timestamps".
- `ref` is present only on a reply — `ack`, `error`, `pong`, `cast.start_result`, and the `hello`
  and `ready` of a `get_state` snapshot. It carries the `id` the client sent, so replies can be
  correlated. Broadcast frames never carry one.
- Client frames use the same envelope but only `type`, optional `id` (string) and optional `data`
  are read. `v` and `ts` from a client are ignored.
- Timestamps inside `data` (`at`, `last_event_at`) are UTC ISO-8601.
- **Forward compatibility rule: a client MUST ignore server message types it does not know, and
  MUST ignore unknown fields inside `data`.** New message types and new fields are added without
  bumping `v`. `v` is bumped only when the envelope changes or an existing type changes meaning.

---

## 3. Server → client messages

### `hello`
First frame on every connection.

```jsonc
"data": {
  "protocol": 1,
  "server_version": "1.4.2",
  "level_interval_ms": 1000,
  "sound_clear_hold_seconds": 30,
  "features": ["monitoring", "sound_state", "sound_level", "global_settings", "rooms", "cast", "webrtc"]
}
```

`features` names the optional capabilities this server supports; a client should feature-test
against it rather than against `server_version`.

### `rooms`
The room list — everything needed to build one HA device per room. Sent in the snapshot and again
whenever the list changes.

```jsonc
"data": {
  "rooms": [
    {
      "id": 1,
      "name": "Nursery",
      "icon": "baby",
      "audio_enabled": true,
      "video_enabled": true,
      "source_type": "rtsp"        // "rtsp" | "google_nest"
    }
  ]
}
```

### `global_settings`
The composed **global** audio settings. Per-room overrides are out of scope for protocol v1 —
these values are global and apply to every room.

```jsonc
"data": {
  "sound_threshold_db": -20.0,     // double
  "threshold_pause_seconds": 30,   // int
  "volume_adjustment_db": -15.0,   // double
  "audio_filter_enabled": false,   // bool
  "average_sample_count": 10,      // int
  "low_pass_hz": 4000,             // int
  "high_pass_hz": 300              // int
}
```

Maps to `number.sound_threshold`, `number.threshold_pause`, `number.volume_adjustment` and
`switch.audio_filter`.

### `active_room`

```jsonc
"data": { "room_id": 1, "name": "Nursery" }   // room_id and name are null when no room is active
```

### `connected_viewers`

```jsonc
"data": { "count": 2 }
```

The number of clients currently connected to the SignalR hub — the web dashboard and the mobile
app. HA WebSocket clients are **not** counted.

### `room_state`
The full per-room state, one frame per room. Sent in the snapshot, and again for a single room
after `set_monitoring`. Incremental changes afterwards arrive as `sound_level`, `sound_state`,
`stream_online` and `monitoring`.

```jsonc
"data": {
  "room_id": 1,
  "monitoring": true,        // bool, the always-on subscription (not "is it running")
  "sound_detected": false,   // bool, the held sound state
  "stream_online": true,     // bool
  "level_db": -41.2          // double|null, last broadcast level; null if none yet
}
```

### `ready`
`"data": null`. Ends the snapshot. Everything before it is state; everything after it is a change.

### `sound_level`
The throttled level broadcast. One frame per tick (`level_interval_ms`, default 1000 ms),
carrying every room that produced audio during the tick. **Never one frame per audio frame.**

The set of rooms therefore varies from tick to tick and is **not** limited to the monitored ones:
a room reports whenever its audio pipeline is running, for whatever reason — monitoring is on, or
the mobile app or the web dashboard is streaming it. See §6.

```jsonc
"data": {
  "levels": [
    { "room_id": 1, "level_db": -38.41 },
    { "room_id": 2, "level_db": -55.02 }
  ]
}
```

**Coalescing rule: the value is the PEAK level over the interval, not the mean or the last
sample.** A short cry between two quiet samples must not be averaged away. Values are rounded to
two decimals. A room with no audio in the interval is omitted from `levels` entirely — it does not
appear with a null. No frame is sent at all when no room produced audio.

Drives `sensor.<room>_sound_level` (dB, `state_class: measurement`).

### `sound_state`
The *held* sound state — the one to drive `binary_sensor.<room>_sound` from. Sent **on change
only**.

```jsonc
"data": {
  "room_id": 1,
  "detected": true,
  "last_event_at": "2026-09-22T12:34:56.789Z"   // string|null
}
```

Semantics: `detected` goes `true` on a threshold crossing and returns to `false`
`sound_clear_hold_seconds` (default 30) after the **last** crossing. A crossing while already
`true` extends the hold and emits no `sound_state` frame — only a `sound_event`.

This hold exists because the underlying backend event is an *edge* muted for
`threshold_pause_seconds` after each fire; a raw edge cannot drive a binary sensor. The hold and
the mute window are independent: with the defaults (30 s pause, 30 s hold) continuous noise
produces a crossing roughly every 30 s, which keeps the state latched `true`.

### `sound_event`
The discrete crossing, for firing `babymonitarr_sound_detected` on the HA bus. Sent on **every**
crossing, including repeats while `sound_state.detected` is already `true`.

```jsonc
"data": {
  "room_id": 1,
  "level_db": -12.3,
  "threshold_db": -20.0,
  "at": "2026-09-22T12:34:56.789Z"
}
```

### `stream_online`
Sent on change. Drives `binary_sensor.<room>_stream_online` (`device_class: connectivity`).

```jsonc
"data": { "room_id": 1, "online": true }
```

`online` is true when an audio frame arrived within `StreamOnlineTimeoutSeconds` (default 10 s),
regardless of why the room's reader is running. Turning monitoring off does not by itself set it
to `false`: if the mobile app or the dashboard is still streaming the room, audio keeps arriving
and the room stays online. It goes `false` once the reader stops — which, with monitoring off, is
when the last viewer leaves. A room nobody is monitoring or streaming reports `online: false`.

### `monitoring`
Sent on change (i.e. in response to some client's `set_monitoring`). Broadcast to **all** HA
clients, not only the requester.

```jsonc
"data": { "room_id": 1, "enabled": true }
```

### `webrtc.answer`
The answer to a `webrtc.offer`. Sent only to the requester, with `ref` set.

```jsonc
"data": {
  "room_id": 1,
  "kind": "video",   // "video" | "audio" — echoes the offer
  "sdp": "v=0\r\no=- ..."
}
```

### `webrtc.candidate`
A server-side ICE candidate, trickled as gathering proceeds. Sent unsolicited after an answer, no
`ref`. Same shape in both directions.

```jsonc
"data": {
  "room_id": 1,
  "kind": "video",
  "candidate": "candidate:1 1 udp 2130706431 192.168.1.10 50000 typ host",
  "sdp_mid": "0",
  "sdp_m_line_index": 0
}
```

The server may send the **same candidate twice with different addresses**: once as gathered, and
once rewritten to the configured advertised address (the `WebRtc:AdvertisedAddress` setting, or the
host inferred from the handshake). Both are real candidates; add both and let ICE pick.

### `webrtc.closed`
The server tore a peer connection down.

```jsonc
"data": { "room_id": 1, "kind": "video", "reason": "Closed by client" }
```

### `cast.devices`
Every Cast receiver the backend knows, from all three discovery origins. Sent in the snapshot,
after any `cast.*` command, and again whenever the list or a device's state changes.

```jsonc
"data": {
  "devices": [
    {
      "device_id": "1f2e3d4c5b6a...",   // string, the mDNS TXT "id"; identity across all origins
      "name": "Living Room TV",         // string
      "model": "Chromecast Ultra",      // string, may be ""
      "host": "192.168.1.42",           // string, last known address
      "port": 8009,                     // int
      "origin": "ha-proxy",             // "discovered" | "manual" | "ha-proxy"
      "manually_added": false,          // bool
      "is_video_capable": true,         // bool, from the "ca" bitmask bit 0x01
      "is_group": false,                // bool, from the "ca" bitmask bit 0x20
      "is_online": true,                // bool
      "last_seen_at": "2026-09-22T12:00:00Z", // string|null
      "casting_room_id": 1,             // int|null, room currently cast to this device
      "last_error": null                // string|null, cleared on a successful start
    }
  ]
}
```

`is_online` is true for a manually-added device, or when `last_seen_at` is within twice the
backend's own discovery interval (floor 120 s). A device pushed by the HA proxy therefore goes
stale if HA stops pushing — see §8.2.

### `cast.state`
Per-room cast state, one frame per room. Sent in the snapshot, after a `cast.*` command that
touched that room, and on change.

```jsonc
"data": {
  "room_id": 1,
  "casting": true,                      // bool, true when at least one session is live
  "targets": ["1f2e3d4c...", "9a8b..."],// string[], the room's SAVED target selection
  "sessions": [                         // the sessions actually running right now
    { "device_id": "1f2e3d4c...", "video": true, "started_at": "2026-09-22T12:00:00Z" }
  ]
}
```

`targets` is the persisted default selection (what `cast.start` uses when given no device ids);
`sessions` is what is live. They are independent — a saved target with no session is not casting.

### `cast.start_result`
The reply to `cast.start`. Sent only to the requester, with `ref` set.

```jsonc
"data": {
  "room_id": 1,
  "started": [
    { "device_id": "1f2e3d4c...", "video": true, "started_at": "2026-09-22T12:00:00Z" }
  ],
  "failed": {                           // device_id -> human-readable reason, may be empty
    "9a8b7c6d...": "Could not reach a Cast device at 192.168.1.50:8009."
  }
}
```

**A partial failure is a success at the protocol level.** `cast.start` never returns `error` for a
device that would not start; the per-device reason appears in `failed` and the devices that did
start appear in `started`. The HA side must surface `failed` rather than treating a non-empty
`started` as "it worked".

### `ack`
Command succeeded. `ref` carries the request `id`.

```jsonc
"data": { "command": "set_monitoring" }
```

### `error`
Command failed, or an unsolicited protocol error. `ref` is the request `id` when the error answers
a request, otherwise `null`.

```jsonc
"data": { "code": "unknown_room", "message": "No room 7" }
```

| `code` | Meaning |
| --- | --- |
| `bad_request` | Malformed JSON, missing `type`, or missing/ill-typed fields in `data`. |
| `unknown_type` | The `type` is not a command this protocol version handles. |
| `unknown_room` | `room_id` does not exist. |
| `unknown_device` | `device_id` is not a device, or has no session to stop. |
| `webrtc_codec_mismatch` | The offer did not contain the codec this room is forwarded in. Actionable: change what the offer lists. |
| `webrtc_failed` | Any other negotiation failure — no source codec, probe timeout, rejected SDP. `message` carries the specific reason. |
| `internal_error` | The command threw. The connection stays open. |

### `pong`
`"data": null`, `ref` echoes the `ping` id.

---

## 4. Client → server messages

All commands accept an optional `"id"` (string). When present it comes back as `ref` on the `ack`
or `error`. Commands are applied in the order received on a connection.

### `ping`
```jsonc
{ "type": "ping", "id": "7" }
```
Answered with `pong`. Application-level liveness; the server also sends WebSocket-level pings.

### `get_state`
```jsonc
{ "type": "get_state", "id": "8" }
```
Re-sends the full snapshot (`hello` … `ready`), with `ref` on `hello` and `ready`. Use it after a
reconnect, or when HA reloads the config entry. Never poll it on a timer.

### `set_monitoring`
```jsonc
{ "type": "set_monitoring", "id": "9", "data": { "room_id": 1, "enabled": true } }
```
Turns the always-on subscription for a room on or off. Drives `switch.<room>_monitoring`.
Answered with a broadcast `monitoring`, a broadcast `room_state` for that room, and an `ack`.

### `set_global_settings`
```jsonc
{ "type": "set_global_settings", "id": "10", "data": { "sound_threshold_db": -25.0 } }
```
Partial update — every field is optional and only the fields present are written. Field names and
types are exactly those of the `global_settings` message. After the write the audio processors are
refreshed and SignalR clients are notified (`SettingsUpdated`), so the web dashboard stays in
step. Answered with a broadcast `global_settings` (if anything changed) and an `ack`.

### `set_active_room`
```jsonc
{ "type": "set_active_room", "id": "11", "data": { "room_id": 2 } }
```
Answered with a broadcast `active_room` and an `ack`; SignalR clients get `ActiveRoomChanged`.
`unknown_room` if the room does not exist.

### `webrtc.offer`
Hands the backend an offer and gets an answer. This is the direction Home Assistant's camera API
uses.

```jsonc
{
  "type": "webrtc.offer",
  "id": "30",
  "data": {
    "room_id": 1,
    "kind": "video",          // optional; "audio" selects audio, anything else (or absent) is video
    "sdp": "v=0\r\no=- ..."
  }
}
```

Answered with `webrtc.answer`, then a stream of `webrtc.candidate` frames. On failure, `error`
with `webrtc_codec_mismatch` or `webrtc_failed` and nothing is left allocated.

Re-offering for a room that already has a peer on this connection **replaces** it: the old peer is
closed first. One peer per `(connection, room, kind)`.

### `webrtc.candidate`
A client ICE candidate. Identical shape to the server→client message. No reply — not even an
`ack`, because candidates are high-rate. A candidate for a peer that no longer exists is dropped
silently rather than raising an error.

```jsonc
{
  "type": "webrtc.candidate",
  "data": {
    "room_id": 1, "kind": "video",
    "candidate": "candidate:...", "sdp_mid": "0", "sdp_m_line_index": 0
  }
}
```

Candidates arriving before the answer is applied are queued server-side and replayed, so there is
no need to wait for `webrtc.answer` before sending them.

### `webrtc.stop`
```jsonc
{ "type": "webrtc.stop", "id": "31", "data": { "room_id": 1, "kind": "video" } }
```
Closes the peer. Answered with `webrtc.closed` and an `ack`. Stopping a peer that does not exist is
a success.

### `cast.discovered`
The mDNS proxy push. Home Assistant runs the `_googlecast._tcp.local.` browser (it has host
networking and can see link-local multicast; the backend on a Docker bridge network cannot) and
pushes what it finds. See §8.2 for the model.

```jsonc
{
  "type": "cast.discovered",
  "id": "20",
  "data": {
    "devices": [
      {
        "id": "1f2e3d4c5b6a...",   // REQUIRED, TXT "id". This is the device identity.
        "host": "192.168.1.42",    // REQUIRED, the resolved address
        "port": 8009,              // optional int, defaults to 8009
        "fn": "Living Room TV",    // optional, TXT "fn" — friendly name
        "md": "Chromecast Ultra",  // optional, TXT "md" — model
        "ca": "4101"               // optional, TXT "ca" — capability bitmask; int or string
      }
    ]
  }
}
```

Send the whole current set, not a delta; the backend upserts and never removes on absence. A
device entry missing `id` or `host` is skipped silently. `bad_request` only if `devices` is absent
or no entry survived that check. Answered with `ack` plus a broadcast `cast.devices`.

Push on browser add/update, and re-push on reconnect. There is no unregister message: a receiver
that disappears simply stops being refreshed and goes `is_online: false`.

### `cast.start`
```jsonc
{ "type": "cast.start", "id": "21", "data": { "room_id": 1, "device_ids": ["1f2e..."] } }
```
`device_ids` is optional; omitted or empty means the room's saved `targets`. If that is also empty
the reply is a `cast.start_result` with empty `started` and `failed` — not an error. Answered with
`cast.start_result` to the caller, plus broadcast `cast.devices` and `cast.state`.

A cast start is **not** cancelled if the HA socket drops while it is in flight; the session is
server-side state.

### `cast.stop`
```jsonc
{ "type": "cast.stop", "id": "22", "data": { "room_id": 1 } }
```
Stops every session for the room. Answered with `ack` plus broadcast `cast.devices` and
`cast.state`. Stopping a room that is not casting is a success, not an error.

### `cast.stop_device`
```jsonc
{ "type": "cast.stop_device", "id": "23", "data": { "device_id": "1f2e..." } }
```
Stops one device without touching the room's other targets. `unknown_device` when the device had
no session. Answered with `ack` plus broadcast `cast.devices` and `cast.state`.

### `cast.set_targets`
```jsonc
{ "type": "cast.set_targets", "id": "24", "data": { "room_id": 1, "device_ids": ["1f2e..."] } }
```
Replaces the room's saved target selection. Does **not** start or stop anything. An empty or
absent `device_ids` clears the selection. Answered with `ack` plus broadcast `cast.state`.

---

## 5. Connect, reconnect, resync

On every accepted connection the server sends, in this order and without being asked:

1. `hello`
2. `rooms`
3. `global_settings`
4. `active_room`
5. `connected_viewers`
6. one `room_state` per room
7. `cast.devices`
8. one `cast.state` per room
9. `ready`

That snapshot is complete: a fresh HA client can create and populate every entity from it alone.
**There is no polling in this protocol.** After `ready`, the server pushes changes.

Reconnect contract:

- **Monitoring state survives a dropped connection.** It lives in the backend, keyed by room, and
  is not cleared when the HA client disconnects — a Home Assistant restart must not open a gap in
  sound detection. The reconnecting client learns the current state from `room_state.monitoring`
  and should reconcile its switch entities to it (or re-assert its own desired state with
  `set_monitoring`).
- **Monitoring is in-memory and resets to off when the backend restarts.** It is not persisted.
  After a backend restart every room starts unmonitored, and the client must re-assert
  `set_monitoring` for each room whose switch is on. `room_state` in the snapshot always tells the
  truth, so a client that reconciles against the snapshot handles this without special-casing.
- **Monitoring defaults OFF for a new room.** A room gets no always-on reader until HA turns
  monitoring on for it. This is intentional.
- Sound state, level and stream-online are derived, not persisted; after a backend restart they
  begin at `false`/`null` and refill as audio arrives.
- **WebRTC peers die with the socket.** When a /ha/ws connection drops, every peer connection it
  owns is closed server-side, mirroring what the SignalR hub does on disconnect. A reconnecting
  client must re-offer; there is no session to resume and no `webrtc.closed` will arrive for peers
  killed this way, because the socket carrying it is already gone.
- **Cast devices and saved targets are persisted**; live cast sessions are not and do not survive a
  backend restart. Proxy-discovered devices keep their last known host, so HA should re-push
  `cast.discovered` on every reconnect.
- Multiple HA clients may connect at once. State changes are broadcast to all of them.
- A client that stops draining its socket (more than `SendQueueCapacity`, default 256, frames
  queued) is closed with WebSocket status 1011 and should reconnect with backoff.

---

## 6. Always-on monitoring — what actually happens

`AudioStreamingService` starts an audio reader on the first subscriber for a room and stops it
when the last one leaves, so sound detection normally runs only while somebody is streaming.

Monitoring fixes that in the plainest way available: with monitoring on, the backend registers an
ordinary `IAudioStreamingService.SubscribeToRoom(roomId, handler)` subscriber — the same kind an
app client registers — and stays subscribed. It never requests a WebRTC peer and never
unsubscribes until monitoring is turned off. There is no second reader lifecycle and no change to
`StopReader` semantics.

Reporting is a separate concern from that subscription. The backend observes levels through a
service-wide event raised by every running room processor, so what HA reports is "whatever audio
pipeline happens to be running", not "the rooms HA subscribed to". The observation is passive: it
starts no reader, and once a room's reader stops the room is dropped rather than left reporting a
stale value.

Consequences worth knowing:

- Monitoring keeps the RTSP/Nest reader (and its ffmpeg pipeline) alive for that room. That is the
  cost of always-on detection; it is why the switch defaults off.
- `sound_level`, `stream_online`, `sound_event` and `sound_state` all fire for a room that is
  **not** monitored, whenever somebody else is streaming it. Monitoring only guarantees that
  detection runs — and therefore that the room reports — when nobody is streaming.
- When the last viewer of an unmonitored room leaves, the reader stops: within
  `StreamOnlineTimeoutSeconds` the room emits `stream_online: false`, stops appearing in
  `sound_level`, and `room_state` reports it with `level_db: null` again.

---

## 7. Configuration

`appsettings.json`, section `Ha`:

```jsonc
"Ha": {
  "LevelBroadcastIntervalMs": 1000,   // sound_level tick; floor 100 ms
  "SoundClearHoldSeconds": 30,        // sound_state hold after the last crossing
  "StreamOnlineTimeoutSeconds": 10,   // silence before a room reports offline
  "StatePollIntervalSeconds": 5,      // re-read of rooms/settings/active room/viewers
  "SendQueueCapacity": 256            // per-connection outbound queue before the client is dropped
}
```

`StatePollIntervalSeconds` is a server-side implementation detail: rooms, global settings, the
active room and the viewer count have no change notification inside the backend, so the server
re-reads them on this interval and pushes only when the payload actually differs. Clients never
poll.

---

## 8. Subsystem notes

The two prefixed message spaces, `webrtc.` and `cast.`, are both implemented; §8.1 and §8.2 are
the models behind their messages, which are specified in §3 and §4 like everything else. §8.3 is
what remains genuinely reserved.

**A v1 client must ignore server frames whose `type` it does not recognise** and unknown fields
inside `data`. That is what lets further message types be added without a version bump.

### 8.1 WebRTC signalling — `webrtc.` — IMPLEMENTED

The messages are specified in §3 (`webrtc.answer`, `webrtc.candidate`, `webrtc.closed`) and §4
(`webrtc.offer`, `webrtc.candidate`, `webrtc.stop`). This section is the model behind them, and it
matters more than usual because the constraints here are not negotiable.

#### Direction: the client offers

The backend's own flow, used by the web dashboard and the mobile app over SignalR, is the
**opposite** of what is exposed here: there the backend calls `createOffer()` and the client
answers. Home Assistant's camera API (`Camera.async_handle_async_webrtc_offer`) hands the
integration an offer and expects a `WebRTCAnswer`, so `/ha/ws` exposes an **answer path** added
alongside the offer path — `CreateVideoAnswer` / `CreateAudioAnswer`. The offer path is untouched
and still serves every existing client.

Practically: **send `webrtc.offer` with whatever SDP Home Assistant gave you.** Do not try to
produce an offer of your own or to reverse the direction.

#### Codec: the answer is passthrough-only, or it fails

Video frames are forwarded from the camera as RTP **without transcoding**. The backend can only
send the codec the camera already produces, decided by `VideoCodecProbeService` — the same source
of truth the offer path uses. So:

1. The room's passthrough codec is resolved (H264, H265 or VP8).
2. That codec is looked up **in your offer**, and the answer carries only it, reusing the payload
   id and `fmtp` your offer proposed for it. Every other codec in your offer is dropped.
3. **If your offer does not list that codec, the offer is rejected** with
   `webrtc_codec_mismatch`, and the message names the room's codec and everything your offer did
   list. Nothing is allocated and nothing falls back to transcoding.

The practical consequence for the integration: an H.265 camera will fail against a browser that
only offers H.264, and that is the correct outcome — the alternative is a connection that
negotiates successfully and then sends frames the peer cannot decode. Surface the error text; it
is written to be read by a human.

Audio follows the same rule with a different shape. A Nest room is raw Opus passthrough and
requires Opus in the offer. An RTSP room is re-encoded, so any codec shared between the offer and
the encoder is acceptable; the answer carries the intersection.

#### Media kinds are separate peer connections

Video and audio are negotiated as **separate peer connections**, selected by `kind`, because the
backend keeps them in separate services with separate peers. One `webrtc.offer` produces one peer
of one kind.

If your offer contains both an audio and a video m-line — which is what a browser-generated camera
offer normally looks like — the backend answers the m-line matching `kind` and leaves the other one
unmatched. **This is the least-tested part of this protocol**: it depends on SIPSorcery rejecting
an m-line it has no local track for rather than refusing the whole description. If you see
`webrtc_failed` with a message like `The WebRTC offer for room N was rejected: ...`, that is what
happened, and the fix on your side is to offer a single media section per `webrtc.offer`.

#### No data channel on this path

The SignalR offer path opens a data channel for audio-level updates. The answer path does not: a
locally-added channel cannot appear in an answer to an offer with no `m=application` section. Use
the `sound_level` message (§3) for levels — it is throttled, per-room, and does not need a peer
connection at all.

#### Lifecycle

- One peer per `(connection, room, kind)`. Re-offering replaces it.
- `webrtc.stop` closes one peer.
- Dropping the socket closes all of that connection's peers, mirroring
  `AudioStreamHub.OnDisconnectedAsync`.
- Closing the last peer for a room releases the video reader if nothing else is using it, so a
  camera that nobody is watching does not keep ffmpeg running.

#### Fallbacks: there are none

The camera entity has native WebRTC and nothing else. See §9 — this was a correction to the
original design, not an omission.

### 8.2 Cast devices and commands — `cast.` — IMPLEMENTED

The messages are specified in §3 (`cast.devices`, `cast.state`, `cast.start_result`) and §4
(`cast.discovered`, `cast.start`, `cast.stop`, `cast.stop_device`, `cast.set_targets`). This
section is the model behind them.

#### Why HA proxies discovery

mDNS is link-local. The backend runs on a Docker bridge network and cannot see the multicast, so
its own `_googlecast._tcp.local.` browse finds nothing unless the container is on host networking.
Home Assistant already has a Zeroconf browser and the network access to use it, so it browses and
pushes the results in over `cast.discovered`.

Only *discovery* needs multicast. Once the address is known, port 8009 is ordinary outbound TCP
that a bridge-network container reaches fine, so the backend keeps talking to receivers directly
with Sharpcaster. Nothing about the media path changes.

#### Identity and dedupe

A device's identity is the mDNS TXT `id`, which is already what the backend stores as its device
id. A receiver seen by the backend's own browse *and* pushed by HA therefore collapses to **one**
device row, not two — no reconciliation is needed on either side.

Manually-added devices are the exception: they have no TXT record, so their id is `host:port`. A
manual entry and a later proxy push for the same physical device will not dedupe.

#### Origins

`origin` records **which path last saw the device**, for diagnostics and UI only. It never affects
identity.

| `origin` | Meaning |
| --- | --- |
| `discovered` | Last seen by the backend's own mDNS browse. |
| `manual` | Added by IP through the BabyMonitarr UI. Sticky: a proxy push never overwrites it. |
| `ha-proxy` | Last seen by Home Assistant's Zeroconf browser. |

A device seen by both browses flips between `discovered` and `ha-proxy` depending on which ran
last. That is intended: the field answers "which path is currently seeing this", not "where did it
come from originally".

#### Address churn

Chromecast addresses move on DHCP renewal. A `cast.discovered` push for a known `id` with a new
`host` **updates the stored address in place** — it never creates a second device. The last known
host is all that is kept; there is no history and no persistence beyond the existing device row.

The proxy does not need to survive HA being offline. If HA is down the backend still has the last
known host and simply tries it; a connect failure surfaces per device in `cast.start_result.failed`
and the address is corrected on the next push. Nothing re-resolves on the backend side.

#### What is deliberately absent

- **No unregister.** HA never tells the backend a receiver went away; it just stops refreshing it
  and `is_online` decays.
- **No HA cast transport.** Casting is driven by Sharpcaster from the backend, as it always was.
  Routing a cast through HA services (to reach Sonos or AirPlay targets Sharpcaster cannot drive)
  is a separate, deferred piece of work and no seam for it exists yet.
- **No change to the existing paths.** Local mDNS discovery, manual add, the SignalR hub's cast
  methods and existing app clients all behave exactly as before.

### 8.3 Other reserved space

- New fields inside an existing `data` object. Clients must ignore unknown fields.
- New values of `error.code`. Treat an unrecognised code as a generic failure.
- Per-room settings overrides, if they ever arrive, will be a separate `room_settings` message and
  a `set_room_settings` command; `global_settings` keeps its current meaning.

---

## 9. Corrections to DESIGN.md

`babymonitarr-hacs/docs/DESIGN.md` is the agreed design. Two of its statements did not survive
contact with the backend code. They are recorded here rather than edited there, and the decisions
below are the ones actually implemented.

### 9.1 "HA native camera WebRTC on the existing offer/answer flow"

The existing flow could not be reused as-is. The backend was always the offerer; HA's camera API
requires the integration to answer. DESIGN.md assumed these matched.

**Resolution:** an answer path was added to `VideoWebRtcService` and `AudioWebRtcService`, additive
and separate from the offer path, and exposed as `webrtc.*`. The passthrough-only codec constraint
is real and is enforced by selecting the room's codec out of the remote offer and failing cleanly
when it is absent — see §8.1.

### 9.2 "HLS and stills as fallback"

Neither exists in the backend.

- **Stills.** `FfprobeSnapshotService` is named misleadingly: it shells out to `ffprobe` to write
  stream metadata into the log when an RTSP open fails. It is a diagnostics helper and never
  produces an image. Nothing else in the backend encodes a frame to JPEG or PNG.
- **HLS.** `CastHlsStreamService` produces HLS only for the lifetime of a cast session, behind a
  random per-session token at `/cast/hls/{token}/index.m3u8`, reference-counted and torn down when
  the last session releases it. There is no room-level HLS endpoint and the token is meaningless
  once casting stops.

**Resolution:** neither was built. The camera entity has native WebRTC only. Building either would
be new backend machinery and a separate decision.
