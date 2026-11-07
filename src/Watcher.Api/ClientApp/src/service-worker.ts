import { isCacheableStaticRequest, staticShellFiles } from './service-worker-policy';

interface ExtendableEventLike extends Event {
  waitUntil(promise: Promise<unknown>): void;
}

interface FetchEventLike extends ExtendableEventLike {
  request: Request;
  respondWith(response: Promise<Response>): void;
}

const worker = self as unknown as {
  clients: { claim(): Promise<void> };
  location: { origin: string };
  skipWaiting(): Promise<void>;
  addEventListener(type: 'install', listener: (event: ExtendableEventLike) => void): void;
  addEventListener(type: 'activate', listener: (event: ExtendableEventLike) => void): void;
  addEventListener(type: 'fetch', listener: (event: FetchEventLike) => void): void;
};
const cacheName = 'kgta-alert-shell-v2';

worker.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(cacheName)
      .then((cache) => cache.addAll([...staticShellFiles]))
      .then(() => worker.skipWaiting()),
  );
});

worker.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter((key) => key.startsWith('kgta-alert-shell-') && key !== cacheName)
            .map((key) => caches.delete(key)),
        ),
      )
      .then(() => worker.clients.claim()),
  );
});

worker.addEventListener('fetch', (event) => {
  const request = event.request;
  if (!isCacheableStaticRequest(request.method, request.url, worker.location.origin)) return;

  event.respondWith(caches.match(request).then((cached) => cached ?? fetch(request)));
});
