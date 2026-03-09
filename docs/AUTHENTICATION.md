# Authentication

BabyMonitarr supports three authentication methods, configured via the `Auth__Method` environment variable (or `Auth:Method` in `appsettings.json`).

## Local Authentication (Default)

```
Auth__Method=Local
```

Users are managed directly in BabyMonitarr. The first user to register becomes the admin. This is the default and requires no additional setup.

## Proxy Header Authentication

```
Auth__Method=ProxyHeader
```

For use with reverse proxy auth solutions like **Authelia**, **Authentik**, **Caddy Security**, or any proxy that sets trusted user headers.

BabyMonitarr reads the authenticated user from HTTP headers set by the proxy:

| Variable | Default | Description |
|----------|---------|-------------|
| `Auth__Proxy__UserHeader` | `Remote-User` | Header containing the username |
| `Auth__Proxy__NameHeader` | `Remote-Name` | Header containing the display name |
| `Auth__Proxy__EmailHeader` | `Remote-Email` | Header containing the email address |

Users are automatically provisioned on first login. The first user becomes the admin.

### Authelia Example

In your Authelia configuration, protect the BabyMonitarr domain and ensure it forwards the `Remote-User`, `Remote-Name`, and `Remote-Email` headers. Authelia does this by default for its auth endpoints.

Docker Compose snippet:

```yaml
services:
  babymonitarr:
    image: ghcr.io/inrego/babymonitarr:latest
    environment:
      - Auth__Method=ProxyHeader
    # ... other config
```

Ensure your reverse proxy passes the auth headers through to BabyMonitarr and that direct access (bypassing the proxy) is blocked at the network level.

### Authentik Example

Authentik uses the same header-based approach. Configure a proxy provider in Authentik that forwards the user headers and set `Auth__Method=ProxyHeader`.

## OIDC (OpenID Connect)

```
Auth__Method=OIDC
```

For direct integration with identity providers like **Authelia**, **Authentik**, **Keycloak**, **Auth0**, or any OIDC-compliant provider.

| Variable | Default | Description |
|----------|---------|-------------|
| `Auth__Oidc__Authority` | *(empty)* | OIDC issuer URL (e.g., `https://auth.example.com`) |
| `Auth__Oidc__ClientId` | *(empty)* | OAuth 2.0 client ID |
| `Auth__Oidc__ClientSecret` | *(empty)* | OAuth 2.0 client secret |
| `Auth__Oidc__Scopes` | `openid profile email` | Space-separated OIDC scopes |

The redirect URI to configure in your identity provider:

```
https://your-babymonitarr-host/signin-oidc
```

Users are automatically provisioned on first OIDC login. The first user becomes the admin.

### Example

```yaml
services:
  babymonitarr:
    image: ghcr.io/inrego/babymonitarr:latest
    environment:
      - Auth__Method=OIDC
      - Auth__Oidc__Authority=https://auth.example.com
      - Auth__Oidc__ClientId=babymonitarr
      - Auth__Oidc__ClientSecret=your-secret
    # ... other config
```

## API Key Authentication

Regardless of the chosen auth method, the mobile app always authenticates using API keys. Generate keys from the **API Keys** page in the web UI. API keys are passed via the `Authorization: Bearer <key>` header or as an `access_token` query parameter (used by SignalR).
