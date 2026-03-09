# Google Nest Setup

BabyMonitarr supports Google Nest cameras via the Smart Device Management (SDM) API with WebRTC streaming.

## Prerequisites

- A Google Nest camera (any model supporting WebRTC via SDM)
- A Google Cloud project with billing enabled
- Access to the [Device Access Console](https://console.nest.google.com/device-access)

> **Note:** Google charges a one-time **US $5 registration fee** to create a Device Access project (Step 3). This is a Google requirement, not a BabyMonitarr fee.

## Step 1: Create a Google Cloud Project

1. Go to the [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project (or use an existing one)
3. Enable the **Smart Device Management API**

## Step 2: Set Up OAuth 2.0 Credentials

1. In Google Cloud Console, go to **APIs & Services** > **Credentials**
2. Click **Create Credentials** > **OAuth 2.0 Client ID**
3. Set application type to **Web application**
4. Add the following authorized redirect URI:
   ```
   http://localhost:8080/nest/auth/callback
   ```
   Replace `localhost:8080` with your actual host/port if different.
5. Note your **Client ID** and **Client Secret**

## Step 3: Create a Device Access Project

1. Go to the [Device Access Console](https://console.nest.google.com/device-access)
2. Create a new project (there is a one-time $5 fee from Google)
3. Link your Google Cloud OAuth client
4. Note your **Project ID**

## Step 4: Configure BabyMonitarr

1. Open BabyMonitarr and go to the **System** page
2. Enter your **Client ID**, **Client Secret**, and **Project ID**
3. Click **Link Account** and complete the Google OAuth flow
4. Your Nest devices will now appear as an option when creating a new monitor

## Troubleshooting

- Ensure your redirect URI matches exactly (including protocol and port)
- The Google account used for OAuth must have access to the Nest devices
- WebRTC streams from Nest cameras are time-limited and will automatically refresh
