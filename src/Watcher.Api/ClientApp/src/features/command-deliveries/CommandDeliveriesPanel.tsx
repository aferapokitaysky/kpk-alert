import { useCallback, useEffect, useRef, useState } from 'react';
import {
  CommandDeliveriesApiError,
  getCommandDeliveries,
  type CommandDelivery,
  type CommandDeliveryState,
  type CommandDeliveriesPage,
} from './api';

type PanelState =
  | { status: 'loading' }
  | { status: 'ready'; data: CommandDeliveriesPage }
  | { status: 'error'; message: string };

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
    if (error.status === 404) return 'Команду або записи доставки не знайдено.';
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

export function CommandDeliveriesPanel({ commandId }: { commandId: string }) {
  const [page, setPage] = useState(1);
  const [state, setState] = useState<PanelState>({ status: 'loading' });
  const activeRequest = useRef<AbortController | null>(null);
  const load = useCallback(async () => {
    activeRequest.current?.abort();
    const controller = new AbortController();
    activeRequest.current = controller;
    setState({ status: 'loading' });

    try {
      const data = await getCommandDeliveries(commandId, page, controller.signal);
      if (controller.signal.aborted) return;
      const lastPage = Math.max(1, Math.ceil(data.totalCount / data.pageSize));
      if (page > lastPage) {
        setPage(lastPage);
        return;
      }
      setState({ status: 'ready', data });
    } catch (error) {
      if (!controller.signal.aborted) setState({ status: 'error', message: errorMessage(error) });
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, [commandId, page]);

  useEffect(() => {
    const requestTimer = window.setTimeout(() => void load(), 0);
    return () => {
      window.clearTimeout(requestTimer);
      activeRequest.current?.abort();
    };
  }, [load]);

  const pageCount =
    state.status === 'ready'
      ? Math.max(1, Math.ceil(state.data.totalCount / state.data.pageSize))
      : 1;

  return (
    <section className="delivery-panel" aria-labelledby="delivery-title">
      <header className="delivery-panel-heading">
        <div>
          <p className="eyebrow">СТАН ВУЗЛІВ</p>
          <h2 id="delivery-title">Доставка команди</h2>
          <p>Статус «Вузол прийняв команду» ще не підтверджує відтворення звуку.</p>
        </div>
        <button
          className="button button-secondary"
          type="button"
          onClick={() => void load()}
          disabled={state.status === 'loading'}
        >
          {state.status === 'loading' ? 'Завантаження…' : 'Оновити'}
        </button>
      </header>

      {state.status === 'loading' && (
        <p className="delivery-message" role="status">
          Завантажуємо статуси доставки…
        </p>
      )}
      {state.status === 'error' && (
        <div className="delivery-message delivery-message-error" role="alert">
          <span>{state.message}</span>
          <button className="inline-button" type="button" onClick={() => void load()}>
            Повторити
          </button>
        </div>
      )}
      {state.status === 'ready' && (
        <>
          {state.data.items.length === 0 ? (
            <p className="delivery-message" role="status">
              {state.data.totalCount === 0
                ? 'Для цієї команди поки немає записів доставки.'
                : 'На цій сторінці записів немає. Оновіть дані або перейдіть на іншу сторінку.'}
            </p>
          ) : (
            <ol className="delivery-list" aria-label="Статуси доставки за вузлами">
              {state.data.items.map((delivery) => (
                <DeliveryItem key={delivery.deviceId} delivery={delivery} />
              ))}
            </ol>
          )}
          <footer className="delivery-pagination">
            <span>
              Вузлів: {state.data.totalCount} · {state.data.pageSize} на сторінці
            </span>
            {pageCount > 1 && (
              <nav className="delivery-page-controls" aria-label="Сторінки доставки">
                <button
                  className="button button-secondary"
                  type="button"
                  onClick={() => setPage((current) => Math.max(1, current - 1))}
                  disabled={state.data.page <= 1}
                >
                  Попередня
                </button>
                <span>
                  Сторінка {state.data.page} з {pageCount}
                </span>
                <button
                  className="button button-secondary"
                  type="button"
                  onClick={() => setPage((current) => Math.min(pageCount, current + 1))}
                  disabled={state.data.page >= pageCount}
                >
                  Наступна
                </button>
              </nav>
            )}
          </footer>
        </>
      )}
    </section>
  );
}
