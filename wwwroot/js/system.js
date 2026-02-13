// SignalR connection
let connection;

document.addEventListener('DOMContentLoaded', function () {
    initializeSignalRConnection();
});

function initializeSignalRConnection() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl("/audioHub")
        .withAutomaticReconnect()
        .build();

    connection.start()
        .then(async () => {
            console.log("SignalR Connected (System)");
            await loadNestSettings();
            await checkNestStatus();

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
