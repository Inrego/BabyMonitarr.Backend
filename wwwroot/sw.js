const CACHE_NAME = 'babymonitarr-v1';
const STATIC_ASSETS = [
    '/',
    '/css/site.css',
    '/js/dashboard.js',
    '/js/site.js',
    '/js/system.js',
    '/images/icon.svg',
    '/images/icon-192.png',
    '/images/icon-512.png',
    '/lib/bootstrap/dist/css/bootstrap.min.css',
    '/lib/jquery/dist/jquery.min.js',
    '/lib/bootstrap/dist/js/bootstrap.bundle.min.js'
];

// Install — pre-cache static assets
self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME).then((cache) => {
            return cache.addAll(STATIC_ASSETS);
        })
    );
    self.skipWaiting();
});

// Activate — clean old caches
self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) => {
            return Promise.all(
                keys.filter((key) => key !== CACHE_NAME)
                    .map((key) => caches.delete(key))
            );
        })
    );
    self.clients.claim();
});

// Fetch — network-first for navigation/API, cache-first for static assets
self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Skip SignalR and API requests — always network
    if (url.pathname.startsWith('/audioHub') || url.pathname.startsWith('/api/')) {
        return;
    }

    // Static assets — cache-first
    if (STATIC_ASSETS.includes(url.pathname) || url.pathname.startsWith('/lib/') || url.pathname.startsWith('/images/')) {
        event.respondWith(
            caches.match(event.request).then((cached) => {
                return cached || fetch(event.request).then((response) => {
                    if (response.ok) {
                        const clone = response.clone();
                        caches.open(CACHE_NAME).then((cache) => cache.put(event.request, clone));
                    }
                    return response;
                });
            })
        );
        return;
    }

    // Navigation — network-first with cache fallback
    if (event.request.mode === 'navigate') {
        event.respondWith(
            fetch(event.request).catch(() => {
                return caches.match('/') || new Response('Offline', { status: 503 });
            })
        );
        return;
    }
});

// Handle messages from main thread for notifications
self.addEventListener('message', (event) => {
    if (event.data && event.data.type === 'SHOW_NOTIFICATION') {
        const { title, body, tag, icon, data } = event.data;
        self.registration.showNotification(title, {
            body: body,
            icon: icon || '/images/icon-192.png',
            badge: '/images/icon-192.png',
            tag: tag,
            data: data,
            silent: false,
            vibrate: [0, 200, 100, 200, 100, 400]
        });
    }
});

// Handle notification click — focus or open the app
self.addEventListener('notificationclick', (event) => {
    event.notification.close();

    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clients) => {
            // Focus existing window if available
            for (const client of clients) {
                if (client.url.includes(self.location.origin) && 'focus' in client) {
                    return client.focus();
                }
            }
            // Otherwise open a new window
            return self.clients.openWindow('/');
        })
    );
});
