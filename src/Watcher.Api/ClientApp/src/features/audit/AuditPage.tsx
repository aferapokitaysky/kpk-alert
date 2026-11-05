import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { ApiError, getAuditEvents, type AuditPage as AuditPageData } from '../../api';
import { pageCount, type SessionState } from '../../state';

type AuditState =
  | { status: 'loading' }
  | { status: 'ready'; data: AuditPageData }
  | { status: 'unauthenticated' }
  | { status: 'forbidden' }
  | { status: 'unavailable' }
  | { status: 'error' };

interface Props {
  session: SessionState;
  onSessionExpired: () => void;
  onSessionRetry: () => void;
}

function formatDate(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(date);
}

function AuditStateMessage({ state, onRetry }: { state: AuditState; onRetry: () => void }) {
  if (state.status === 'loading')
    return (
      <div className="state-card" role="status">
        Завантажуємо журнал дій…
      </div>
    );
  if (state.status === 'unauthenticated')
    return (
      <div className="state-card state-denied">
        Сесію завершено. <a href="/api/v1/auth/login">Увійдіть через Google</a>, щоб продовжити.
      </div>
    );
  if (state.status === 'forbidden')
    return (
      <div className="state-card state-denied" role="alert">
        Журнал доступний лише аудиторам і адміністраторам.
      </div>
    );
  if (state.status === 'unavailable')
    return (
      <div className="state-card state-denied" role="alert">
        Не вдалося відкрити журнал. Сервер не надає запитані дані.
      </div>
    );
  if (state.status === 'error')
    return (
      <div className="state-card state-error" role="alert">
        <span>Не вдалося завантажити журнал дій.</span>
        <button className="inline-button" type="button" onClick={onRetry}>
          Повторити
        </button>
      </div>
    );
  return null;
}

export function AuditPage({ session, onSessionExpired, onSessionRetry }: Props) {
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [filterDraft, setFilterDraft] = useState('');
  const [eventType, setEventType] = useState('');
  const [state, setState] = useState<AuditState>({ status: 'loading' });
  const requestSequence = useRef(0);
  const canView = session.status === 'authenticated' && session.capabilities.canViewAudit;

  const load = useCallback(async () => {
    if (!canView) return;
    const sequence = ++requestSequence.current;
    setState({ status: 'loading' });
    try {
      const data = await getAuditEvents(page, pageSize, eventType);
      if (sequence !== requestSequence.current) return;
      setState({ status: 'ready', data });
    } catch (error) {
      if (sequence !== requestSequence.current) return;
      if (error instanceof ApiError && error.status === 401) {
        setState({ status: 'unauthenticated' });
        onSessionExpired();
      } else if (error instanceof ApiError && error.status === 403) {
        setState({ status: 'forbidden' });
      } else if (error instanceof ApiError && (error.status === 404 || error.status === 501)) {
        setState({ status: 'unavailable' });
      } else {
        setState({ status: 'error' });
      }
    }
  }, [canView, eventType, onSessionExpired, page, pageSize]);

  useEffect(() => {
    const requestTimer =
      session.status === 'authenticated' && canView
        ? window.setTimeout(() => void load(), 0)
        : undefined;
    return () => {
      if (requestTimer !== undefined) window.clearTimeout(requestTimer);
      requestSequence.current += 1;
    };
  }, [canView, load, session.status]);

  const displayState: AuditState =
    session.status === 'unauthenticated'
      ? { status: 'unauthenticated' }
      : session.status === 'error'
        ? { status: 'error' }
        : session.status === 'loading'
          ? { status: 'loading' }
          : !canView
            ? { status: 'forbidden' }
            : state;
  const retry = () => {
    if (session.status === 'authenticated' && canView) void load();
    else onSessionRetry();
  };
  const maxPage =
    displayState.status === 'ready' ? pageCount(displayState.data.totalCount, pageSize) : 1;
  const submitFilter = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setPage(1);
    setEventType(filterDraft.trim());
  };

  return (
    <>
      <section className="page-heading">
        <div>
          <p className="eyebrow">КГТА · КОНТРОЛЬ ДІЙ</p>
          <h1>Журнал дій</h1>
          <p className="lead">Події системи у зворотному хронологічному порядку.</p>
        </div>
      </section>
      <section className="panel audit-panel" aria-labelledby="audit-title">
        <div className="panel-heading">
          <div>
            <p className="eyebrow">ІСТОРІЯ</p>
            <h2 id="audit-title">Події аудиту</h2>
          </div>
          {displayState.status === 'ready' && (
            <span className="selection-count">Записів: {displayState.data.totalCount}</span>
          )}
        </div>
        {session.status === 'authenticated' && canView && (
          <form className="audit-filters" onSubmit={submitFilter}>
            <label className="form-field">
              <span>Тип події</span>
              <input
                className="form-control"
                value={filterDraft}
                onChange={(event) => setFilterDraft(event.target.value)}
                placeholder="Наприклад, auth.login.succeeded"
                maxLength={100}
              />
            </label>
            <label className="form-field page-size-field">
              <span>На сторінці</span>
              <select
                className="form-control form-control-sm"
                value={pageSize}
                onChange={(event) => {
                  setPageSize(Number(event.target.value));
                  setPage(1);
                }}
              >
                <option value={25}>25</option>
                <option value={50}>50</option>
                <option value={100}>100</option>
              </select>
            </label>
            <button className="button button-primary" type="submit">
              Застосувати фільтр
            </button>
            {eventType && (
              <button
                className="button button-secondary"
                type="button"
                onClick={() => {
                  setFilterDraft('');
                  setEventType('');
                  setPage(1);
                }}
              >
                Скинути
              </button>
            )}
          </form>
        )}
        {displayState.status === 'ready' ? (
          displayState.data.items.length === 0 ? (
            <div className="state-card">За вибраними умовами подій немає.</div>
          ) : (
            <div className="audit-list" aria-live="polite">
              {displayState.data.items.map((item) => (
                <article className="audit-event" key={item.id}>
                  <div className="audit-event-heading">
                    <span className="event-type">{item.eventType}</span>
                    <time dateTime={item.occurredAtUtc}>{formatDate(item.occurredAtUtc)}</time>
                  </div>
                  <p className="audit-actor">
                    {item.actorType === 'user' ? 'Користувач' : item.actorType} ·{' '}
                    {item.actorId ?? 'системна дія'}
                  </p>
                  <details>
                    <summary>Деталі події</summary>
                    <pre>{JSON.stringify(item.payload, null, 2)}</pre>
                  </details>
                </article>
              ))}
            </div>
          )
        ) : (
          <AuditStateMessage state={displayState} onRetry={retry} />
        )}
        {displayState.status === 'ready' && (
          <nav className="pagination" aria-label="Сторінки журналу">
            <button
              className="button button-secondary"
              type="button"
              disabled={page <= 1}
              onClick={() => setPage((current) => Math.max(1, current - 1))}
            >
              Попередня
            </button>
            <span>
              Сторінка {page} з {maxPage}
            </span>
            <button
              className="button button-secondary"
              type="button"
              disabled={page >= maxPage}
              onClick={() => setPage((current) => Math.min(maxPage, current + 1))}
            >
              Наступна
            </button>
          </nav>
        )}
      </section>
    </>
  );
}
