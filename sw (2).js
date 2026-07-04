// ============================================================
// SERVICE WORKER - Natanael POS
// Cachea el "cascarón" de la app (index.html, manifest, íconos)
// para que abra al instante desde el navegador o instalada.
// Las llamadas a Google Sheets/Apps Script NUNCA se cachean acá:
// eso ya lo maneja el propio index.html con su caché en
// localStorage + sincronización en segundo plano.
// ============================================================

const CACHE_NAME = 'natanael-pos-cache-v1';

const APP_SHELL = [
    './',
    './index.html',
    './manifest.json',
    './icon-192.png',
    './icon-512.png'
];

// Instalación: precachea el cascarón de la app
self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => cache.addAll(APP_SHELL))
            .then(() => self.skipWaiting())
    );
});

// Activación: limpia cachés viejas de versiones anteriores
self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(
                keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k))
            ))
            .then(() => self.clients.claim())
    );
});

// Fetch: estrategia "stale-while-revalidate"
// 1) Si hay algo en caché, lo devuelve DE INMEDIATO (carga instantánea)
// 2) En paralelo pide la versión nueva a internet y la guarda para
//    la próxima vez (así con el tiempo siempre se actualiza sola)
self.addEventListener('fetch', (event) => {
    const req = event.request;

    if (req.method !== 'GET') return;

    const url = new URL(req.url);

    // Nunca tocar las llamadas a Google Sheets / Apps Script:
    // deben ir siempre directo a la red, sin pasar por este caché.
    if (url.hostname.includes('script.google.com') ||
        url.hostname.includes('script.googleusercontent.com')) {
        return;
    }

    // Solo cachear recursos propios de la app (mismo origen)
    if (url.origin !== self.location.origin) return;

    event.respondWith(
        caches.open(CACHE_NAME).then((cache) =>
            cache.match(req).then((cachedResp) => {
                const networkFetch = fetch(req)
                    .then((networkResp) => {
                        if (networkResp && networkResp.status === 200) {
                            cache.put(req, networkResp.clone());
                        }
                        return networkResp;
                    })
                    .catch(() => cachedResp);

                // Si ya hay caché, se devuelve al toque. Si no, se espera la red.
                return cachedResp || networkFetch;
            })
        )
    );
});
