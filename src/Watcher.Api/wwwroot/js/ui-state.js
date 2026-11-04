export function listState({ status, count }) {
  if (status === null) return 'loading';
  if (status === 401) return 'unauthenticated';
  if (status === 403) return 'forbidden';
  if (status >= 200 && status < 300) return count > 0 ? 'ready' : 'empty';
  return 'error';
}

export function healthState(responseStatus, payload) {
  return responseStatus >= 200 && responseStatus < 300 && payload?.status === 'ok'
    ? 'available'
    : 'unavailable';
}

export function sidebarIsInert(isMobile, isOpen) {
  return isMobile && !isOpen;
}
