import { useCallback, useEffect, useRef, useState } from 'react';
import {
  DeviceStatusApiError,
  getDeviceStatus,
  type DeviceStatus,
  type DeviceStatusPage as PageData,
} from './api';
import './device-status.css';

type Status = 'loading' | 'ready' | 'error';

function formatTime(value: string | null): string {
  if (!value) return 'Немає збережених даних';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 'Немає збережених даних';
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(date);
}

function errorMessage(error: unknown): string {
  if (error instanceof DeviceStatusApiError) {
    if (error.status === 401) return 'Сесію завершено. Увійдіть знову, щоб продовжити.';
    if (error.status === 403)
      return 'Перегляд пристроїв доступний лише адміністраторам і аудиторам.';
  }
  return 'Не вдалося завантажити пристрої. Перевірте з’єднання та повторіть спробу.';
}

function DeviceCard({ device }: { device: DeviceStatus }) {
  const statusClass = device.isConnected ? 'is-connected' : 'is-disconnected';
  return (
    <li className="device-status-card">
      <article>
        <header className="device-status-card-heading">
          <div>
            <h2>{device.name}</h2>
            <p>{device.locationName}</p>
          </div>
          <span className={`device-connection ${statusClass}`}>
            <span aria-hidden="true" />
            {device.isConnected ? 'Підключений до сервера' : 'Немає з’єднання'}
          </span>
        </header>
        <dl className="device-status-details">
          <div>
            <dt>Пристрій</dt>
            <dd>{device.isActive ? 'Активний' : 'Відкликаний'}</dd>
          </div>
          <div>
            <dt>Корпус</dt>
            <dd>{device.locationIsActive ? 'Активний' : 'Неактивний'}</dd>
          </div>
          <div>
            <dt>Остання збережена активність</dt>
            <dd>
              {device.lastSeenAtUtc ? (
                <time dateTime={device.lastSeenAtUtc}>{formatTime(device.lastSeenAtUtc)}</time>
              ) : (
                formatTime(null)
              )}
            </dd>
          </div>
        </dl>
      </article>
    </li>
  );
}

export function DeviceStatusPage() {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<Status>('loading');
  const [data, setData] = useState<PageData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const activeRequest = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    if (document.hidden || activeRequest.current) return;

    const controller = new AbortController();
    activeRequest.current = controller;
    const { signal } = controller;
    setStatus('loading');
    try {
      const result = await getDeviceStatus(page, signal);
      if (signal.aborted) return;
      const lastAvailablePage = Math.max(1, Math.ceil(result.totalCount / result.pageSize));
      if (page > lastAvailablePage) {
        setPage(lastAvailablePage);
        return;
      }
      setData(result);
      setError(null);
      setStatus('ready');
    } catch (requestError) {
      if (signal.aborted) return;
      setError(errorMessage(requestError));
      setStatus('error');
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, [page]);

  useEffect(() => {
    let timer: number | null = null;

    const pausePolling = () => {
      if (timer !== null) {
        window.clearInterval(timer);
        timer = null;
      }

      activeRequest.current?.abort();
      activeRequest.current = null;
    };

    const resumePolling = () => {
      if (document.hidden || timer !== null) return;

      void load();
      timer = window.setInterval(() => {
        if (!document.hidden) void load();
      }, 15_000);
    };

    const handleVisibilityChange = () => {
      if (document.hidden) pausePolling();
      else resumePolling();
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    resumePolling();

    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      pausePolling();
    };
  }, [load]);

  const lastPage = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;

  return (
    <section className="device-status-page" aria-busy={status === 'loading'}>
      <header className="device-status-heading">
        <div>
          <p className="eyebrow">ОБЛАДНАННЯ</p>
          <h1>Стан пристроїв</h1>
          <p className="device-status-lead">
            Підключення показує лише активний WebSocket до цього екземпляра сервера. Воно не
            підтверджує стан аудіосистеми чи відтворення звуку.
          </p>
        </div>
        <button
          className="button button-secondary"
          type="button"
          aria-disabled={status === 'loading'}
          onClick={() => status !== 'loading' && void load()}
        >
          {status === 'loading' ? 'Завантаження…' : 'Оновити'}
        </button>
      </header>

      {status === 'loading' && (
        <p className="device-status-message" role="status">
          {data ? 'Оновлюємо список пристроїв…' : 'Завантажуємо список пристроїв…'}
        </p>
      )}
      {error && (
        <div className="device-status-message device-status-error" role="alert">
          <span>{error}</span>
          <button type="button" className="inline-button" onClick={() => void load()}>
            Повторити
          </button>
        </div>
      )}
      {status === 'ready' && data?.items.length === 0 && (
        <p className="device-status-message" role="status">
          {data.totalCount === 0
            ? 'Зареєстрованих пристроїв поки немає.'
            : 'На цій сторінці пристроїв немає. Перейдіть на попередню сторінку.'}
        </p>
      )}
      {data && (data.items.length > 0 || data.totalCount > 0) && (
        <>
          <p className="device-status-count">Пристроїв: {data.totalCount}</p>
          {data.items.length > 0 && (
            <ul className="device-status-list" aria-label="Зареєстровані пристрої">
              {data.items.map((device, index) => (
                <DeviceCard
                  key={`${device.locationName}:${device.name}:${index}`}
                  device={device}
                />
              ))}
            </ul>
          )}
          {lastPage > 1 && (
            <nav className="device-status-pagination" aria-label="Сторінки пристроїв">
              <button
                className="button button-secondary"
                type="button"
                disabled={status === 'loading' || page <= 1}
                onClick={() => setPage((current) => Math.max(1, current - 1))}
              >
                Попередня
              </button>
              <span aria-live="polite">
                Сторінка {data.page} з {lastPage}
              </span>
              <button
                className="button button-secondary"
                type="button"
                disabled={status === 'loading' || page >= lastPage}
                onClick={() => setPage((current) => Math.min(lastPage, current + 1))}
              >
                Наступна
              </button>
            </nav>
          )}
        </>
      )}
    </section>
  );
}
