import { useCallback, useEffect, useRef, useState } from 'react';
import {
  CommandDeliveriesApiError,
  getCommandDeliveries,
  type CommandDelivery,
  type CommandDeliveryState,
  type CommandDeliveriesPage,
} from './api';
import type { Location } from '../../api';

type PanelStatus = 'loading' | 'ready' | 'error';

const stateLabels: Record<CommandDeliveryState, string> = {
  Pending: 'Очікує надсилання',
  InFlight: 'Надсилається',
  Accepted: 'Вузол прийняв команду',
  Playing: 'Звук відтворюється',
  Completed: 'Вузол завершив команду',
  Stopped: 'Вузол зупинив команду',
  Rejected: 'Вузол відхилив команду',
  Failed: 'Вузол повідомив про помилку',
  Expired: 'Строк дії команди минув',
};

function errorMessage(error: unknown): string {
  if (error instanceof CommandDeliveriesApiError) {
    if (error.status === 401) return 'Сесію завершено. Увійдіть знову, щоб продовжити.';
    if (error.status === 403) return 'У вас немає доступу до стану доставки цієї команди.';
    if (error.status === 404) return 'Команду не знайдено або немає доступу до її стану доставки.';
  }
  return 'Не вдалося завантажити статуси доставки. Перевірте з’єднання та повторіть спробу.';
}

function formatTime(value: string): string {
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(new Date(value));
}

function DeliveryItem({ delivery }: { delivery: CommandDelivery }) {
  const className = delivery.state.toLowerCase().replace(/[^a-z]/g, '-');

  return (
    <li className={`delivery-item delivery-item-${className}`}>
      <article>
        <header className="delivery-item-heading">
          <h3>{delivery.deviceName}</h3>
          <span className={`delivery-state delivery-state-${className}`}>
            {stateLabels[delivery.state]}
          </span>
        </header>
        {delivery.state === 'Accepted' && (
          <p className="delivery-state-note">
            Прийняття команди не означає, що звук уже відтворюється.
          </p>
        )}
        <dl className="delivery-details">
          <div>
            <dt>Створено</dt>
            <dd>
              <time dateTime={delivery.createdAtUtc}>{formatTime(delivery.createdAtUtc)}</time>
            </dd>
          </div>
          <div>
            <dt>Дійсна до</dt>
            <dd>
              <time dateTime={delivery.expiresAtUtc}>{formatTime(delivery.expiresAtUtc)}</time>
            </dd>
          </div>
          <div>
            <dt>Остання спроба</dt>
            <dd>
              {delivery.lastAttemptAtUtc ? (
                <time dateTime={delivery.lastAttemptAtUtc}>
                  {formatTime(delivery.lastAttemptAtUtc)}
                </time>
              ) : (
                'Ще не було'
              )}
            </dd>
          </div>
          <div>
            <dt>Спроб надсилання</dt>
            <dd>{delivery.attemptCount}</dd>
          </div>
        </dl>
      </article>
    </li>
  );
}

interface Props {
  commandId: string;
  commandAction?: 'start' | 'stop';
  canOperate?: boolean;
  targetLocations?: Location[];
  onRequestStop?: (commandId: string, locations: Location[]) => void;
}

export function CommandDeliveriesPanel({
  commandId,
  commandAction = 'start',
  canOperate = false,
  targetLocations = [],
  onRequestStop,
}: Props) {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<PanelStatus>('loading');
  const [data, setData] = useState<CommandDeliveriesPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const activeRequest = useRef<AbortController | null>(null);
  const load = useCallback(async () => {
    if (document.hidden || activeRequest.current) return;

    const controller = new AbortController();
    activeRequest.current = controller;
    setStatus('loading');

    try {
      const data = await getCommandDeliveries(commandId, page, controller.signal);
      if (controller.signal.aborted) return;
      const lastPage = Math.max(1, Math.ceil(data.totalCount / data.pageSize));
      if (page > lastPage) {
        setPage(lastPage);
        return;
      }
      setData(data);
      setError(null);
      setStatus('ready');
    } catch (error) {
      if (!controller.signal.aborted) {
        setError(errorMessage(error));
        setStatus('error');
      }
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, [commandId, page]);

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

  const pageCount = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;
  const hasStopCandidate = status === 'ready' && data !== null && data.hasUnfinishedDeliveries;
  const canStop =
    canOperate && hasStopCandidate && targetLocations.length > 0 && onRequestStop !== undefined;
  const summary = data?.stateSummary;
  const stopConfirmed = summary ? summary.stopped + summary.completed : 0;
  const waitingCount = summary
    ? summary.pending + summary.inFlight + summary.accepted + summary.playing
    : 0;
  const startAwaitingCount = summary ? summary.pending + summary.inFlight + summary.accepted : 0;
  const unconfirmedCount = summary ? summary.rejected + summary.failed + summary.expired : 0;

  return (
    <section
      className="delivery-panel"
      aria-labelledby="delivery-title"
      aria-busy={status === 'loading'}
    >
      <header className="delivery-panel-heading">
        <div>
          <p className="eyebrow">СТАН ВУЗЛІВ</p>
          <h2 id="delivery-title">Доставка команди</h2>
          <p>Статус «Вузол прийняв команду» ще не підтверджує відтворення звуку.</p>
        </div>
        <button
          className="button button-secondary"
          type="button"
          aria-disabled={status === 'loading'}
          onClick={() => status !== 'loading' && void load()}
        >
          {status === 'loading' ? 'Завантаження…' : status === 'error' ? 'Повторити' : 'Оновити'}
        </button>
      </header>

      {status === 'loading' && (
        <p className="delivery-message" role="status">
          {data ? 'Оновлюємо статуси доставки…' : 'Завантажуємо статуси доставки…'}
        </p>
      )}
      {error && (
        <div className="delivery-message delivery-message-error" role="alert">
          <span>{error}</span>
        </div>
      )}
      {data && (
        <>
          {commandAction === 'stop' && (
            <p className="delivery-message" role="status" aria-live="polite">
              {data.totalCount === 0
                ? 'Для команди відбою немає доставок до вузлів.'
                : waitingCount > 0
                  ? `Підтверджено обробку відбою на ${stopConfirmed} із ${data.totalCount} вузлів. Очікують відповіді: ${waitingCount}.${unconfirmedCount > 0 ? ` Без підтвердження: ${unconfirmedCount}.` : ''}`
                  : unconfirmedCount > 0
                    ? `Підтверджено обробку відбою на ${stopConfirmed} із ${data.totalCount} вузлів. Без підтвердження: ${unconfirmedCount}.`
                    : `Усі вузли підтвердили обробку відбою: ${stopConfirmed} із ${data.totalCount}.`}
            </p>
          )}
          {commandAction === 'start' && data.totalCount > 0 && summary && (
            <p className="delivery-message">
              Програмне відтворення підтверджено на {summary.playing} із {data.totalCount} вузлів.{' '}
              Завершили команду: {summary.completed}; зупинено: {summary.stopped}. Очікують
              відповіді: {startAwaitingCount}. Без підтвердження: {unconfirmedCount}.
            </p>
          )}
          {data.items.length === 0 ? (
            <p className="delivery-message" role="status">
              {data.totalCount === 0
                ? 'Для цієї команди поки немає записів доставки.'
                : 'На цій сторінці записів немає. Оновіть дані або перейдіть на іншу сторінку.'}
            </p>
          ) : (
            <ol className="delivery-list" aria-label="Статуси доставки за вузлами">
              {data.items.map((delivery) => (
                <DeliveryItem key={delivery.deviceId} delivery={delivery} />
              ))}
            </ol>
          )}
          <footer className="delivery-pagination">
            <span>
              Вузлів: {data.totalCount} · {data.pageSize} на сторінці
            </span>
            {pageCount > 1 && (
              <nav className="delivery-page-controls" aria-label="Сторінки доставки">
                <button
                  className="button button-secondary"
                  type="button"
                  aria-disabled={status === 'loading' || data.page <= 1}
                  onClick={() => {
                    if (status !== 'loading' && data.page > 1) {
                      setPage((current) => Math.max(1, current - 1));
                    }
                  }}
                >
                  Попередня
                </button>
                <span aria-live="polite" aria-atomic="true">
                  Сторінка {data.page} з {pageCount}
                </span>
                <button
                  className="button button-secondary"
                  type="button"
                  aria-disabled={status === 'loading' || data.page >= pageCount}
                  onClick={() => {
                    if (status !== 'loading' && data.page < pageCount) {
                      setPage((current) => Math.min(pageCount, current + 1));
                    }
                  }}
                >
                  Наступна
                </button>
              </nav>
            )}
          </footer>
        </>
      )}
      {canOperate && targetLocations.length > 0 && onRequestStop && (
        <div className="delivery-actions">
          {canStop ? (
            <button
              className="button button-danger"
              type="button"
              onClick={() => onRequestStop(commandId, targetLocations)}
            >
              Відбій для цієї команди
            </button>
          ) : (
            <p className="delivery-action-note">
              {status === 'ready' && data && !data.hasUnfinishedDeliveries
                ? 'Для цієї команди немає незавершених доставок.'
                : 'Оновіть статуси доставки, щоб перевірити доступність відбою.'}
            </p>
          )}
        </div>
      )}
    </section>
  );
}
