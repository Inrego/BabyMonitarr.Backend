# BabyMonitarr ↔ Home Assistant WebSocket protocol

Version **1**. Implemented in `Ha/` (`HaWebSocketEndpoint.cs`, `HaConnection.cs`,
`HaMonitoringService.cs`, `HaMessages.cs`). This document is the contract; if code and document
disagree, that is a bug in one of them.

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
- `ref` is present only on `ack`, `error`, `pong` and on a `get_state` snapshot. It carries the
  `id` the client sent, so replies can be correlated.
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
  "features": ["monitoring", "sound_state", "sound_level", "global_settings", "rooms"]
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
  "monitoring": true,        // bool, the always-on subscription
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

`online` is true when the room is monitored **and** an audio frame arrived within
`StreamOnlineTimeoutSeconds` (default 10 s). Turning monitoring off sets it to `false`: with no
subscriber the backend stops the reader, so there is nothing to observe. A room that has never
been monitored reports `online: false`.

### `monitoring`
Sent on change (i.e. in response to some client's `set_monitoring`). Broadcast to **all** HA
clients, not only the requester.

```jsonc
"data": { "room_id": 1, "enabled": true }
```

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

---

## 5. Connect, reconnect, resync

On every accepted connection the server sends, in this order and without being asked:

1. `hello`
2. `rooms`
3. `global_settings`
4. `active_room`
5. `connected_viewers`
6. one `room_state` per room
7. `ready`

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

Consequences worth knowing:

- Monitoring keeps the RTSP/Nest reader (and its ffmpeg pipeline) alive for that room. That is the
  cost of always-on detection; it is why the switch defaults off.
- `sound_event` / `sound_state` also fire for a room that is **not** monitored, whenever somebody
  else is streaming it — the backend's threshold event is global. Monitoring only guarantees that
  detection runs when nobody is streaming.

---

## 7. Configuration

`appsettings.json`, section `Ha`:

```jsonc
"Ha": {
  "LevelBroadcastIntervalMs": 1000,   // sound_level tick; floor 100 ms
  "SoundClearHoldSeconds": 30,        // sound_state hold after the last crossing
  "StreamOnlineTimeoutSeconds": 10,   // silence before a monitored room reports offline
  "StatePollIntervalSeconds": 5,      // re-read of rooms/settings/active room/viewers
  "SendQueueCapacity": 256            // per-connection outbound queue before the client is dropped
}
```

`StatePollIntervalSeconds` is a server-side implementation detail: rooms, global settings, the
active room and the viewer count have no change notification inside the backend, so the server
re-reads them on this interval and pushes only when the payload actually differs. Clients never
poll.

---

## 8. Extension points

These are reserved now so a later task can add them without a version bump. **A v1 client must
ignore server frames whose `type` it does not recognise**, which is what makes this safe.

### 8.1 WebRTC signalling — reserved prefix `webrtc.`

Not implemented. Reserved for the HA native camera entity's offer/answer flow, mirroring what
`AudioStreamHub` already does over SignalR.

- Client → server: `webrtc.offer`, `webrtc.answer`, `webrtc.candidate`, `webrtc.stop`
- Server → client: `webrtc.offer`, `webrtc.answer`, `webrtc.candidate`, `webrtc.closed`
- Expected `data` shape: `{ "room_id": int, "session_id": string, ... }` — `session_id` scopes a
  negotiation so several cameras can negotiate on one socket.
- The `hello.features` list will gain `"webrtc"` when this lands. Feature-test on that.

Until then, a `webrtc.*` command is answered with `error` / `unknown_type`.

### 8.2 Cast devices and commands — reserved prefix `cast.`

Not implemented. Reserved for cast device/target state and the `babymonitarr.cast_room` /
`stop_cast` services, and for the HA-side mDNS proxy pushing discovered receivers in.

- Server → client: `cast.devices` (the known receivers), `cast.state` (per room: casting, targets)
- Client → server: `cast.start`, `cast.stop`, `cast.set_targets`, `cast.discovered` (mDNS proxy
  push: address, port and the `id`/`fn`/`md`/`ca` TXT fields)
- Per-room cast state will be delivered as its own `cast.state` message rather than as new fields
  on `room_state`, so `room_state` stays stable.
- The `hello.features` list will gain `"cast"` when this lands.

Until then, a `cast.*` command is answered with `error` / `unknown_type`.

### 8.3 Other reserved space

- New fields inside an existing `data` object. Clients must ignore unknown fields.
- New values of `error.code`. Treat an unrecognised code as a generic failure.
- Per-room settings overrides, if they ever arrive, will be a separate `room_settings` message and
  a `set_room_settings` command; `global_settings` keeps its current meaning.
