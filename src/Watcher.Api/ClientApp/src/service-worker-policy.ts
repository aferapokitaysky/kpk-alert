export const staticShellFiles = [
  '/',
  '/index.html',
  '/manifest.webmanifest',
  '/dist/operator.js',
  '/dist/operator.css',
  '/images/kpk-logo.png',
  '/images/kpk-logo-192.png',
  '/images/kpk-logo-512.png',
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
