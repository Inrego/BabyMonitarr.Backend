# Google Home Setup for Nest Talkback

Push-to-talk plays a parent's voice out of a Nest camera's speaker. The official SDM API used for
monitoring ([Google Nest Setup](GOOGLE_NEST_SETUP.md)) cannot do that, so talkback goes through Google
Home's private "Foyer" API instead. That API only accepts a Google first-party sign-in, which you
capture once from a browser and paste into BabyMonitarr.

> **Read this first.** The Foyer API is undocumented and private. Using it is against Google's API
> terms, and Google can change or block it at any time. BabyMonitarr only uses it for talkback: if the
> credential expires or the API breaks, push-to-talk is disabled and **monitoring keeps working**.

## What you capture

Two values from one browser request:

1. The **Request URL** of `accounts.google.com/o/oauth2/iframerpc?action=issueToken…`
2. That request's **Cookie** request header

BabyMonitarr exchanges them for a one-hour access token, refreshes it on its own, and stores the
cookies Google rotates on each exchange, so the capture keeps working without you. Both values are
kept on the server only: they are never shown again, sent to the app, or written to the logs.

## Capture steps

1. Open a browser **profile you will keep signed in** (Firefox or Safari tend to keep a capture alive
   longer than Chrome, which binds some cookies to the device).
2. Allow third-party cookies for `home.nest.com` (otherwise the page loops with *Nest scope is missing*).
3. Go to <https://home.nest.com>, open DevTools → **Network**, tick **Preserve log**, and type
   `issueToken` into the filter.
4. Click **Sign in with Google** and sign in with the account that owns the cameras.
5. Select the `iframerpc?action=issueToken…` request:
   - copy its **Request URL** (Headers → General), and
   - copy the value of its **cookie** request header (Headers → Request Headers).
6. In BabyMonitarr open **System → Google Home (Talkback)**, paste both, and click **Save & Test**.
   The status turns **Linked** when Google accepts it.
7. Don't sign out of that browser session afterwards: signing out revokes the captured cookies.

## Mapping rooms to cameras

Each Nest room's talkback camera is found automatically in your Google Home graph: the camera in the
Google Home room with the same name as the room the camera has in the Nest/SDM setup, or the only
camera on the account. When that is ambiguous, pick the camera under **Talkback Camera** in the room's
configuration and save.

## Volume

How loud the voice plays in the room is set per room from the app (0–200 %). The camera's own
speaker level is not changed; BabyMonitarr scales the voice and limits peaks so a boost does not clip.

## Troubleshooting

| Status / app message | What to do |
|---|---|
| *Not configured* | Capture and save the credential (above). |
| *Failing: Google rejected the credential* | The browser session ended or expired. Capture a new one. |
| *Choose which camera talkback plays on* | Pick the camera under **Talkback Camera** in the room's configuration. |
| Talk button shows a camera error | The camera or Google did not answer in time; pressing again retries. |

The protocol and the app contract are described in [TALKBACK.md](TALKBACK.md).
