// SignalR connection
let connection;

document.addEventListener('DOMContentLoaded', function () {
    initializeSignalRConnection();
});

function initializeSignalRConnection() {
    // SIGNALR_RETRY_POLICY comes from signalr-retry.js — retries forever.
    connection = new signalR.HubConnectionBuilder()
        .withUrl("/audioHub")
        .withAutomaticReconnect(SIGNALR_RETRY_POLICY)
        .build();

    connection.onclose((error) => {
        console.warn("SignalR closed, restarting:", error?.message ?? error);
        setTimeout(initializeSignalRConnection, 2000);
    });

    connection.start()
        .then(async () => {
            console.log("SignalR Connected (System)");
            await loadNestSettings();
            await checkNestStatus();
            await loadGoogleHomeStatus();

            // Check for OAuth callback result
            const params = new URLSearchParams(window.location.search);
            const nestAuth = params.get('nestAuth');
            if (nestAuth === 'success') {
                showMessage("Google Nest account linked successfully");
                await checkNestStatus();
                // Clean up URL
                window.history.replaceState({}, document.title, window.location.pathname);
            } else if (nestAuth === 'error') {
                showMessage("Failed to link Google Nest account", true);
                window.history.replaceState({}, document.title, window.location.pathname);
            }
        })
        .catch(err => {
            console.error(err);
            setTimeout(initializeSignalRConnection, 5000);
        });
}

async function loadNestSettings() {
    try {
        const settings = await connection.invoke("GetNestSettings");
        if (settings) {
            const clientIdInput = document.getElementById('nestClientId');
            if (clientIdInput) clientIdInput.value = settings.clientId || '';

            const clientSecretInput = document.getElementById('nestClientSecret');
            if (clientSecretInput && settings.clientSecret) clientSecretInput.value = settings.clientSecret;

            const projectIdInput = document.getElementById('nestProjectId');
            if (projectIdInput) projectIdInput.value = settings.projectId || '';
        }
    } catch (err) {
        console.error("Error loading Nest settings:", err);
    }
}

async function saveNestCredentials() {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        showMessage("Not connected to server.", true);
        return;
    }

    const settings = {
        clientId: document.getElementById('nestClientId')?.value || '',
        clientSecret: document.getElementById('nestClientSecret')?.value || '',
        projectId: document.getElementById('nestProjectId')?.value || ''
    };

    try {
        await connection.invoke("UpdateNestSettings", settings);
        showMessage("Credentials saved");
    } catch (err) {
        console.error("Error saving Nest settings:", err);
        showMessage("Error saving credentials", true);
    }
}

async function linkNestAccount() {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        showMessage("Not connected to server.", true);
        return;
    }

    try {
        const authUrl = await connection.invoke("GetNestAuthUrl");
        if (authUrl) {
            window.open(authUrl, '_blank');
        }
    } catch (err) {
        console.error("Error getting Nest auth URL:", err);
        showMessage("Error starting OAuth flow. Make sure credentials are saved first.", true);
    }
}

async function unlinkNestAccount() {
    if (!confirm("Unlink your Google Nest account? You will need to re-authorize to use Nest cameras.")) {
        return;
    }

    try {
        const response = await fetch('/nest/auth/unlink', { method: 'POST' });
        if (response.ok) {
            showMessage("Account unlinked");
            await checkNestStatus();
        } else {
            showMessage("Error unlinking account", true);
        }
    } catch (err) {
        console.error("Error unlinking Nest account:", err);
        showMessage("Error unlinking account", true);
    }
}

async function checkNestStatus() {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;

    try {
        const isLinked = await connection.invoke("IsNestLinked");
        const badge = document.getElementById('nestLinkStatus');
        const text = document.getElementById('nestLinkStatusText');

        if (badge && text) {
            if (isLinked) {
                badge.className = 'status-badge active';
                badge.style.fontSize = '0.85rem';
                badge.style.marginTop = '8px';
                text.textContent = 'Linked';
            } else {
                badge.className = 'status-badge offline';
                badge.style.fontSize = '0.85rem';
                badge.style.marginTop = '8px';
                text.textContent = 'Not Linked';
            }
        }
    } catch (err) {
        console.error("Error checking Nest status:", err);
    }
}

// ===== Google Home credential (Nest talkback) =====
async function loadGoogleHomeStatus() {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;

    try {
        renderGoogleHomeStatus(await connection.invoke("GetGoogleHomeStatus"));
    } catch (err) {
        console.error("Error loading Google Home status:", err);
    }
}

function renderGoogleHomeStatus(status) {
    const badge = document.getElementById('googleHomeStatus');
    const text = document.getElementById('googleHomeStatusText');
    const detail = document.getElementById('googleHomeStatusDetail');
    if (!badge || !text || !detail) return;

    let label = 'Not configured';
    let cls = 'status-badge offline';
    if (status.linked) {
        label = 'Linked';
        cls = 'status-badge active';
    } else if (status.configured) {
        label = status.lastErrorAtUtc ? 'Failing' : 'Not tested yet';
    }

    badge.className = cls;
    badge.style.fontSize = '0.85rem';
    badge.style.marginTop = '8px';
    text.textContent = label;

    const parts = [];
    if (status.configured && status.message && !status.linked) parts.push(status.message);
    if (status.lastSuccessAtUtc) parts.push(`Last token: ${new Date(status.lastSuccessAtUtc + (status.lastSuccessAtUtc.endsWith('Z') ? '' : 'Z')).toLocaleString()}`);
    detail.textContent = parts.join(' · ');
}

async function saveGoogleHomeCredentials() {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        showMessage("Not connected to server.", true);
        return;
    }

    const urlInput = document.getElementById('googleHomeIssueTokenUrl');
    const cookieInput = document.getElementById('googleHomeCookie');
    const button = document.getElementById('saveGoogleHomeBtn');

    try {
        if (button) button.disabled = true;
        const status = await connection.invoke("SetGoogleHomeCredentials", urlInput.value, cookieInput.value);
        urlInput.value = '';
        cookieInput.value = '';
        renderGoogleHomeStatus(status);
        showMessage(status.linked ? "Google Home credential works" : "Saved, but Google rejected it", !status.linked);
    } catch (err) {
        console.error("Error saving Google Home credential:", err);
        showMessage(err?.message?.replace(/^.*HubException: /, '') || "Error saving credential", true);
    } finally {
        if (button) button.disabled = false;
    }
}

async function clearGoogleHomeCredentials() {
    if (!confirm("Remove the Google Home credential? Push-to-talk stops working until a new one is added.")) {
        return;
    }

    try {
        await connection.invoke("ClearGoogleHomeCredentials");
        showMessage("Google Home credential removed");
        await loadGoogleHomeStatus();
    } catch (err) {
        console.error("Error removing Google Home credential:", err);
        showMessage("Error removing credential", true);
    }
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
