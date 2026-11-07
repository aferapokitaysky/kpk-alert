export const staticShellFiles = [
  '/',
  '/index.html',
  '/manifest.webmanifest',
  '/dist/operator.js',
  '/dist/operator.css',
  '/dist/assets/inter-cyrillic-wght-normal.woff2',
  '/dist/assets/inter-cyrillic-ext-wght-normal.woff2',
  '/dist/assets/inter-latin-wght-normal.woff2',
  '/images/kpk-logo.png',
  '/images/kpk-logo-192.png',
  '/images/kpk-logo-512.png',
  '/images/kpk-logo-maskable.png',
] as const;

const staticShellFileSet = new Set<string>(staticShellFiles);

export function isCacheableStaticRequest(
  method: string,
  requestUrl: string,
  origin: string,
): boolean {
  if (method !== 'GET') return false;
  const url = new URL(requestUrl);
  return (
    url.origin === origin &&
    !url.pathname.startsWith('/api/') &&
    staticShellFileSet.has(url.pathname)
  );
}
