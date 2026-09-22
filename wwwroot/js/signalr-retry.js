// Shared SignalR reconnect policy for every page that opens a hub connection.
//
// The bare `withAutomaticReconnect()` default retries at [0, 2s, 10s, 30s] and
// then gives up permanently, leaving the page silently dead until a manual
// refresh. That is the wrong trade for a baby monitor: the reverse proxy in
// front of this app drops every in-flight WebSocket whenever it reloads its
// config, so a client that stops retrying is a client that stops monitoring.
//
// Delays mirror the Flutter client's SignalRService.reconnectDelayForAttempt so
// both clients behave the same way after a drop.
const SIGNALR_RETRY_DELAYS_MS = [0, 2000, 5000, 10000, 15000];

// Never returns null, so the SignalR client retries forever.
function signalrRetryDelay(previousRetryCount) {
    const index = Math.min(previousRetryCount, SIGNALR_RETRY_DELAYS_MS.length - 1);
    const base = SIGNALR_RETRY_DELAYS_MS[index];
    // Jitter keeps several open tabs from stampeding the hub in lockstep after
    // a proxy reload drops all of them at the same instant.
    return base === 0 ? 0 : base + Math.floor(Math.random() * 1000);
}

const SIGNALR_RETRY_POLICY = {
    nextRetryDelayInMilliseconds: (retryContext) =>
        signalrRetryDelay(retryContext.previousRetryCount)
};
