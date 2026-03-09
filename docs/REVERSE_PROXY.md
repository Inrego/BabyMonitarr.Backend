# Reverse Proxy Setup

BabyMonitarr uses SignalR (WebSocket) for signaling and WebRTC for media streaming. When running behind a reverse proxy, the signaling traffic goes through the proxy, but **WebRTC media packets (UDP) must be directly reachable** from the client.

## Key Concepts

- **SignalR/WebSocket**: Runs over HTTP — works through any reverse proxy that supports WebSockets
- **WebRTC media**: Uses UDP — negotiated via ICE and sent directly between client and server, bypassing the proxy
- Even on LAN, audio/video packets will fail if no UDP media ports are reachable from the client

## Docker Compose Example

```yaml
services:
  babymonitarr:
    image: ghcr.io/inrego/babymonitarr:latest
    container_name: babymonitarr
    networks:
      - web
    ports:
      - "40000-40099:40000-40099/udp"
    environment:
      - WebRtc__InferAdvertisedAddressFromForwardedHost=true
      - WebRtc__RtpPortRange__Start=40000
      - WebRtc__RtpPortRange__End=40099
      - WebRtc__RtpPortRange__Shuffle=true
    volumes:
      - babymonitarr-data:/app/data
    restart: unless-stopped

volumes:
  babymonitarr-data:

networks:
  web:
    external: true
```

## Important Environment Variables

| Variable | Description |
|----------|-------------|
| `WebRtc__InferAdvertisedAddressFromForwardedHost` | Set `true` to derive the ICE advertised address from the reverse proxy `Host` / `X-Forwarded-Host` header |
| `WebRtc__AdvertisedAddress` | Override if forwarded-host inference resolves to the wrong address. Set to your LAN IP or public FQDN |
| `WebRtc__RtpPortRange__Start` / `End` | Deterministic UDP port range — must match the Docker port mapping |
| `WebRtc__RtpPortRange__Shuffle` | Randomize port selection within the range |

## Reverse Proxy Configuration

Ensure your reverse proxy:

1. **Forwards WebSocket connections** for SignalR (`/audioHub`)
2. **Passes `Host` or `X-Forwarded-Host` headers** so BabyMonitarr can advertise the correct ICE address
3. **Does not block UDP** on the media port range (these bypass the proxy at the network level)

### Caddy Example

```
babymonitarr.example.com {
    reverse_proxy babymonitarr:8080
}
```

Caddy handles WebSocket upgrade and forwarded headers automatically.

### Nginx Example

```nginx
server {
    listen 443 ssl;
    server_name babymonitarr.example.com;

    location / {
        proxy_pass http://babymonitarr:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### Traefik

Traefik handles WebSocket upgrades automatically. Ensure your router forwards to port 8080 and that standard forwarded headers middleware is enabled.
