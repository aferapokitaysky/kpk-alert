import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { ApiError, getLocations, type Location } from '../../api';
import {
  DeviceStatusApiError,
  getDeviceStatus,
  registerDevice,
  revokeDevice,
  type DeviceStatus,
  type DeviceStatusPage as PageData,
} from './api';
import './device-status.css';

type Status = 'loading' | 'ready' | 'error';
const noSessionExpiredHandler = () => undefined;

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
    if (error.status === 409) return 'Сертифікат уже зареєстрований. Перевірте його відбиток.';
    if (error.status === 422) return 'Оберіть активний корпус і перевірте дані пристрою.';
    if (error.status === 404) return 'Пристрій не знайдено. Оновіть список.';
  }
  return 'Не вдалося завантажити пристрої. Перевірте з’єднання та повторіть спробу.';
}

function mutationErrorMessage(error: unknown): string {
  if (error instanceof DeviceStatusApiError && error.status === 403) {
    return 'Дію дозволено лише активному глобальному адміністратору.';
  }
  if (error instanceof DeviceStatusApiError && error.status === 400) {
    return 'Перевірте назву вузла та SHA-256 відбиток сертифіката.';
  }
  return errorMessage(error);
}

function DeviceCard({
  device,
  canManage,
  pending,
  confirming,
  onRequestRevoke,
  onCancelRevoke,
  onRevoke,
}: {
  device: DeviceStatus;
  canManage: boolean;
  pending: boolean;
  confirming: boolean;
  onRequestRevoke: () => void;
  onCancelRevoke: () => void;
  onRevoke: () => void;
}) {
  const revokeButtonRef = useRef<HTMLButtonElement>(null);
  const confirmationButtonRef = useRef<HTMLButtonElement>(null);
  const wasConfirming = useRef(false);
  const statusClass = device.isConnected ? 'is-connected' : 'is-disconnected';

  useEffect(() => {
    if (confirming) {
      confirmationButtonRef.current?.focus();
    } else if (wasConfirming.current) {
      revokeButtonRef.current?.focus();
    }
    wasConfirming.current = confirming;
  }, [confirming]);

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
            <dd>{device.isActive ? 'Активний' : 'Доступ заблоковано'}</dd>
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
        {canManage && device.isActive && (
          <div className="device-status-actions">
            {confirming ? (
              <div
                className="device-revoke-confirmation"
                role="group"
                aria-label={`Відкликання ${device.name}`}
              >
                <p>Вузол одразу втратить доступ до сервера. Цю дію не можна скасувати.</p>
                <button
                  ref={confirmationButtonRef}
                  className="button button-danger"
                  type="button"
                  disabled={pending}
                  onClick={onRevoke}
                >
                  {pending ? 'Блокуємо доступ…' : 'Підтвердити блокування'}
                </button>
                <button
                  className="button button-secondary"
                  type="button"
                  disabled={pending}
                  onClick={onCancelRevoke}
                >
                  Скасувати
                </button>
              </div>
            ) : (
              <button
                ref={revokeButtonRef}
                className="button button-danger"
                type="button"
                disabled={pending}
                aria-label={`Заблокувати доступ пристрою «${device.name}»`}
                onClick={onRequestRevoke}
              >
                Заблокувати доступ
              </button>
            )}
          </div>
        )}
      </article>
    </li>
  );
}

export function DeviceStatusPage({
  canManage = false,
  onSessionExpired = noSessionExpiredHandler,
}: {
  canManage?: boolean;
  onSessionExpired?: () => void;
}) {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<Status>('loading');
  const [data, setData] = useState<PageData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [locations, setLocations] = useState<Location[]>([]);
  const [locationsLoading, setLocationsLoading] = useState(canManage);
  const [locationLoadError, setLocationLoadError] = useState(false);
  const [draft, setDraft] = useState({ name: '', locationId: '', certificateSha256: '' });
  const [pendingRegistration, setPendingRegistration] = useState(false);
  const [pendingRevocationId, setPendingRevocationId] = useState<string | null>(null);
  const [confirmationId, setConfirmationId] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ kind: 'success' | 'error'; text: string } | null>(null);
  const noticeRef = useRef<HTMLParagraphElement>(null);
  const focusNoticeAfterLoad = useRef(false);
  const activeRequest = useRef<AbortController | null>(null);

  const load = useCallback(
    async (force = false) => {
      if (document.hidden) return;
      if (activeRequest.current && !force) return;
      activeRequest.current?.abort();

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
    },
    [page],
  );

  useEffect(() => {
    if (focusNoticeAfterLoad.current && status !== 'loading') {
      noticeRef.current?.focus();
      focusNoticeAfterLoad.current = false;
    }
  }, [data, status]);

  useEffect(() => {
    if (!canManage) return;
    const controller = new AbortController();
    void getLocations()
      .then((result) => {
        if (!controller.signal.aborted) {
          setLocations(result);
          setLocationLoadError(false);
          setLocationsLoading(false);
        }
      })
      .catch((requestError: unknown) => {
        if (controller.signal.aborted) return;
        if (requestError instanceof ApiError && requestError.status === 401) onSessionExpired();
        setLocationLoadError(true);
        setLocationsLoading(false);
      });
    return () => controller.abort();
  }, [canManage, onSessionExpired]);

  async function submitRegistration(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPendingRegistration(true);
    setNotice(null);
    try {
      await registerDevice({
        name: draft.name.trim(),
        locationId: draft.locationId,
        certificateSha256: draft.certificateSha256.trim(),
      });
      setDraft({ name: '', locationId: '', certificateSha256: '' });
      setNotice({ kind: 'success', text: 'Пристрій зареєстровано.' });
      await load(true);
    } catch (requestError) {
      if (requestError instanceof DeviceStatusApiError && requestError.status === 401) {
        onSessionExpired();
      }
      setNotice({ kind: 'error', text: mutationErrorMessage(requestError) });
    } finally {
      setPendingRegistration(false);
    }
  }

  async function confirmRevocation(device: DeviceStatus) {
    setPendingRevocationId(device.deviceId);
    setNotice(null);
    try {
      await revokeDevice(device.deviceId);
      setConfirmationId(null);
      setNotice({ kind: 'success', text: `Доступ пристрою «${device.name}» заблоковано.` });
      focusNoticeAfterLoad.current = true;
      await load(true);
    } catch (requestError) {
      if (requestError instanceof DeviceStatusApiError && requestError.status === 401) {
        onSessionExpired();
      }
      setNotice({ kind: 'error', text: mutationErrorMessage(requestError) });
    } finally {
      setPendingRevocationId(null);
    }
  }

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

      {canManage && (
        <section className="device-registration-panel" aria-labelledby="device-registration-title">
          <div>
            <p className="eyebrow">НОВИЙ ВУЗОЛ</p>
            <h2 id="device-registration-title">Зареєструвати пристрій</h2>
            <p>
              Сертифікат має бути виданий довіреним центром і перевірений адміністратором. Приватний
              ключ сюди не завантажується.
            </p>
          </div>
          <form
            className="device-registration-form"
            onSubmit={(event) => void submitRegistration(event)}
          >
            <label>
              Назва вузла
              <input
                required
                maxLength={120}
                value={draft.name}
                onChange={(event) =>
                  setDraft((current) => ({ ...current, name: event.target.value }))
                }
              />
            </label>
            <label>
              Корпус
              <select
                required
                value={draft.locationId}
                disabled={locationsLoading || locationLoadError || locations.length === 0}
                onChange={(event) =>
                  setDraft((current) => ({ ...current, locationId: event.target.value }))
                }
              >
                <option value="">Оберіть корпус</option>
                {locations.map((location) => (
                  <option key={location.id} value={location.id}>
                    {location.name}
                  </option>
                ))}
              </select>
            </label>
            <label>
              SHA-256 відбиток сертифіката
              <input
                required
                inputMode="text"
                autoComplete="off"
                maxLength={64}
                minLength={64}
                pattern="[0-9a-fA-F]{64}"
                aria-describedby="device-fingerprint-help"
                value={draft.certificateSha256}
                onChange={(event) =>
                  setDraft((current) => ({ ...current, certificateSha256: event.target.value }))
                }
              />
            </label>
            <span id="device-fingerprint-help" className="device-fingerprint-help">
              64 шістнадцяткові символи. Не вставляйте приватний ключ.
            </span>
            {locationsLoading && <p role="status">Завантажуємо корпуси…</p>}
            {locationLoadError && (
              <p className="device-status-error" role="alert">
                Не вдалося завантажити корпуси. Оновіть сторінку.
              </p>
            )}
            {!locationsLoading && !locationLoadError && locations.length === 0 && (
              <p role="status">Немає доступних активних корпусів.</p>
            )}
            <button
              className="button button-primary"
              type="submit"
              disabled={
                pendingRegistration ||
                locationsLoading ||
                locationLoadError ||
                locations.length === 0
              }
            >
              {pendingRegistration ? 'Реєструємо…' : 'Зареєструвати пристрій'}
            </button>
          </form>
        </section>
      )}

      {notice && (
        <p
          ref={noticeRef}
          tabIndex={-1}
          className={`device-status-notice is-${notice.kind}`}
          role={notice.kind === 'error' ? 'alert' : 'status'}
        >
          {notice.text}
        </p>
      )}

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
              {data.items.map((device) => (
                <DeviceCard
                  key={device.deviceId}
                  device={device}
                  canManage={canManage}
                  pending={pendingRevocationId === device.deviceId}
                  confirming={confirmationId === device.deviceId}
                  onRequestRevoke={() => setConfirmationId(device.deviceId)}
                  onCancelRevoke={() => setConfirmationId(null)}
                  onRevoke={() => void confirmRevocation(device)}
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
