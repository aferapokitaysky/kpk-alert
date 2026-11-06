import { useCallback, useEffect, useRef, useState } from 'react';
import { getDeviceConnectionStatuses, type DeviceConnectionStatus } from '../../api';
import type { SessionState } from '../../state';

export function DeviceConnectionPanel({ session }: { session: SessionState }) {
  const canView = session.status === 'authenticated' && session.capabilities.canOperate;
  const [devices, setDevices] = useState<DeviceConnectionStatus[]>([]);
  const [loading, setLoading] = useState(true);
  const [hasLoaded, setHasLoaded] = useState(false);
  const [error, setError] = useState(false);
  const activeRequest = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    if (!canView || document.hidden || activeRequest.current) return;
    const controller = new AbortController();
    activeRequest.current = controller;
    setLoading(true);
    setError(false);
    try {
      const result = await getDeviceConnectionStatuses(controller.signal);
      if (!controller.signal.aborted) {
        setDevices(result);
        setHasLoaded(true);
      }
    } catch {
      if (!controller.signal.aborted) setError(true);
    } finally {
      if (activeRequest.current === controller) {
        activeRequest.current = null;
        setLoading(false);
      }
    }
  }, [canView]);

  useEffect(() => {
    if (!canView) return;
    let timer: number | null = null;
    const pause = () => {
      if (timer !== null) window.clearInterval(timer);
      timer = null;
      activeRequest.current?.abort();
      activeRequest.current = null;
    };
    const resume = () => {
      if (document.hidden || timer !== null) return;
      void load();
      timer = window.setInterval(() => void load(), 15_000);
    };
    const onVisibilityChange = () => (document.hidden ? pause() : resume());
    document.addEventListener('visibilitychange', onVisibilityChange);
    resume();
    return () => {
      document.removeEventListener('visibilitychange', onVisibilityChange);
      pause();
    };
  }, [canView, load]);

  if (!canView) return null;

  const connectedCount = devices.filter((device) => device.connectionState === 'connected').length;
  const summary = `${connectedCount} з ${devices.length} на зв’язку`;

  return (
    <section className="panel node-panel" aria-labelledby="nodes-title" aria-busy={loading}>
      <div className="panel-heading">
        <div>
          <p className="eyebrow">ЗВ’ЯЗОК З ОБЛАДНАННЯМ</p>
          <h2 id="nodes-title">Підключення вузлів</h2>
          <p>Показано поточне з’єднання з Watcher. Це не підтверджує стан аудіо.</p>
        </div>
        <button
          className="button button-secondary"
          type="button"
          disabled={loading}
          onClick={() => void load()}
        >
          {loading ? 'Оновлення…' : error ? 'Повторити' : 'Оновити'}
        </button>
      </div>
      {loading && !hasLoaded && <p className="state-card">Завантажуємо підключення вузлів…</p>}
      {error && (
        <p className="state-card state-error">
          Не вдалося завантажити статус підключень.
          {hasLoaded && ' Показано останню отриману відповідь.'}
        </p>
      )}
      {hasLoaded && (
        <>
          <p className="device-status-summary" role="status">
            {summary}
          </p>
          {devices.length === 0 ? (
            <p className="state-card">Для доступних корпусів зареєстрованих вузлів немає.</p>
          ) : (
            <ul className="device-status-list">
              {devices.map((device) => (
                <li key={device.deviceId}>
                  <div>
                    <strong>{device.deviceName}</strong>
                    <span>{device.locationName}</span>
                  </div>
                  <span className={`device-connection-state is-${device.connectionState}`}>
                    {device.connectionState === 'connected' ? 'Підключений' : 'Не підключений'}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}
