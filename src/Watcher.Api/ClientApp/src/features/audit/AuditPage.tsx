import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import {
  ApiError,
  getAuditEvents,
  type AuditEventFilters,
  type AuditPage as AuditPageData,
} from '../../api';
import { GoogleLoginLink } from '../auth/GoogleLoginLink';
import { MinuteOfSilenceHistoryPanel } from '../minute-of-silence/MinuteOfSilenceHistoryPanel';
import { MinuteOfSilenceStatusPanel } from '../minute-of-silence/MinuteOfSilenceStatusPanel';
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

type KyivTimeResolution =
  { status: 'valid'; utcIso: string } | { status: 'invalid' | 'nonexistent' | 'ambiguous' };

interface WallTimeParts {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
}

const kyivDateFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: 'Europe/Kyiv',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

function getKyivWallTimeParts(timestamp: number): WallTimeParts {
  const parts = kyivDateFormatter.formatToParts(new Date(timestamp));
  const value = (type: Intl.DateTimeFormatPartTypes) =>
    Number(parts.find((part) => part.type === type)?.value);
  return {
    year: value('year'),
    month: value('month'),
    day: value('day'),
    hour: value('hour'),
    minute: value('minute'),
  };
}

function sameWallTime(left: WallTimeParts, right: WallTimeParts): boolean {
  return (
    left.year === right.year &&
    left.month === right.month &&
    left.day === right.day &&
    left.hour === right.hour &&
    left.minute === right.minute
  );
}

function resolveKyivWallTime(value: string): KyivTimeResolution {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value);
  if (!match) return { status: 'invalid' };

  const requested: WallTimeParts = {
    year: Number(match[1]),
    month: Number(match[2]),
    day: Number(match[3]),
    hour: Number(match[4]),
    minute: Number(match[5]),
  };
  const wallClockAsUtc = new Date(0);
  wallClockAsUtc.setUTCFullYear(requested.year, requested.month - 1, requested.day);
  wallClockAsUtc.setUTCHours(requested.hour, requested.minute, 0, 0);
  if (
    !sameWallTime(requested, {
      year: wallClockAsUtc.getUTCFullYear(),
      month: wallClockAsUtc.getUTCMonth() + 1,
      day: wallClockAsUtc.getUTCDate(),
      hour: wallClockAsUtc.getUTCHours(),
      minute: wallClockAsUtc.getUTCMinutes(),
    })
  ) {
    return { status: 'invalid' };
  }

  const wallClockTimestamp = wallClockAsUtc.getTime();
  const possibleOffsets = new Set<number>();
  const searchWindow = 36 * 60 * 60 * 1000;
  const sampleStep = 6 * 60 * 60 * 1000;
  // Перевіряємо зміщення по обидва боки переходу, щоб знайти пропущений або повторний час.
  for (
    let sample = wallClockTimestamp - searchWindow;
    sample <= wallClockTimestamp + searchWindow;
    sample += sampleStep
  ) {
    const observed = getKyivWallTimeParts(sample);
    const observedAsUtc = new Date(0);
    observedAsUtc.setUTCFullYear(observed.year, observed.month - 1, observed.day);
    observedAsUtc.setUTCHours(observed.hour, observed.minute, 0, 0);
    possibleOffsets.add(observedAsUtc.getTime() - sample);
  }

  const matchingInstants = new Set<number>();
  for (const offset of possibleOffsets) {
    const candidate = wallClockTimestamp - offset;
    if (sameWallTime(getKyivWallTimeParts(candidate), requested)) {
      matchingInstants.add(candidate);
    }
  }

  if (matchingInstants.size === 0) return { status: 'nonexistent' };
  if (matchingInstants.size > 1) return { status: 'ambiguous' };
  const [matchingInstant] = matchingInstants;
  return matchingInstant === undefined
    ? { status: 'invalid' }
    : { status: 'valid', utcIso: new Date(matchingInstant).toISOString() };
}

function isUuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
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
        Сесію завершено.{' '}
        <GoogleLoginLink className="audit-login-link">Увійдіть через Google</GoogleLoginLink>, щоб
        продовжити.
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
  const [actorDraft, setActorDraft] = useState('');
  const [fromDraft, setFromDraft] = useState('');
  const [toDraft, setToDraft] = useState('');
  const [eventType, setEventType] = useState('');
  const [filters, setFilters] = useState<AuditEventFilters>({});
  const [filterError, setFilterError] = useState<string | null>(null);
  const [state, setState] = useState<AuditState>({ status: 'loading' });
  const requestSequence = useRef(0);
  const canView = session.status === 'authenticated' && session.capabilities.canViewAudit;

  const load = useCallback(async () => {
    if (!canView) return;
    const sequence = ++requestSequence.current;
    setState({ status: 'loading' });
    try {
      const data = await getAuditEvents(page, pageSize, eventType, filters);
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
  }, [canView, eventType, filters, onSessionExpired, page, pageSize]);

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
    const actorId = actorDraft.trim();
    if (actorId.length > 0 && !isUuid(actorId)) {
      setFilterError('Введіть ID актора у форматі UUID.');
      return;
    }

    if (Boolean(fromDraft) !== Boolean(toDraft)) {
      setFilterError('Укажіть і початок, і кінець часового діапазону.');
      return;
    }

    const fromResolution = fromDraft ? resolveKyivWallTime(fromDraft) : null;
    const toResolution = toDraft ? resolveKyivWallTime(toDraft) : null;
    if (fromResolution?.status === 'invalid' || toResolution?.status === 'invalid') {
      setFilterError('Перевірте дату й час початку та завершення.');
      return;
    }
    if (fromResolution?.status === 'nonexistent' || toResolution?.status === 'nonexistent') {
      setFilterError(
        'У Києві цей час не існував через перехід на літній час. Оберіть інше значення.',
      );
      return;
    }
    if (fromResolution?.status === 'ambiguous' || toResolution?.status === 'ambiguous') {
      setFilterError(
        'У Києві цей час повторюється через перехід на зимовий час. Оберіть інший час.',
      );
      return;
    }
    if (fromResolution?.status === 'valid' && toResolution?.status === 'valid') {
      const fromTimestamp = Date.parse(fromResolution.utcIso);
      const toTimestamp = Date.parse(toResolution.utcIso);
      if (fromTimestamp >= toTimestamp) {
        setFilterError('Початок діапазону має бути раніше за завершення.');
        return;
      }
      if (toTimestamp - fromTimestamp > 90 * 24 * 60 * 60 * 1000) {
        setFilterError('Діапазон не може перевищувати 90 днів.');
        return;
      }
    }

    const nextFilters: AuditEventFilters = {};
    if (actorId) nextFilters.actorId = actorId;
    if (fromResolution?.status === 'valid' && toResolution?.status === 'valid') {
      nextFilters.fromUtc = fromResolution.utcIso;
      nextFilters.toUtc = toResolution.utcIso;
    }
    setFilterError(null);
    setPage(1);
    setEventType(filterDraft.trim());
    setFilters(nextFilters);
  };

  const hasFilters = eventType.length > 0 || Object.keys(filters).length > 0;
  const clearFilters = () => {
    setFilterDraft('');
    setActorDraft('');
    setFromDraft('');
    setToDraft('');
    setEventType('');
    setFilters({});
    setFilterError(null);
    setPage(1);
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
      <MinuteOfSilenceStatusPanel session={session} />
      <MinuteOfSilenceHistoryPanel
        session={session}
        onSessionExpired={onSessionExpired}
        onSessionRetry={onSessionRetry}
      />
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
          <>
            <form className="audit-filters" onSubmit={submitFilter}>
              <label className="form-field">
                <span>Тип події</span>
                <input
                  className="form-control"
                  value={filterDraft}
                  placeholder="Наприклад, auth.login.succeeded"
                  maxLength={100}
                  onChange={(event) => {
                    setFilterDraft(event.target.value);
                    setFilterError(null);
                  }}
                />
              </label>
              <label className="form-field">
                <span>ID актора</span>
                <input
                  className="form-control"
                  value={actorDraft}
                  onChange={(event) => {
                    setActorDraft(event.target.value);
                    setFilterError(null);
                  }}
                  placeholder="UUID користувача або системи"
                  maxLength={36}
                  autoComplete="off"
                />
              </label>
              <label className="form-field">
                <span>Від (час Києва)</span>
                <input
                  className="form-control"
                  type="datetime-local"
                  value={fromDraft}
                  onChange={(event) => {
                    setFromDraft(event.target.value);
                    setFilterError(null);
                  }}
                />
              </label>
              <label className="form-field">
                <span>До (час Києва)</span>
                <input
                  className="form-control"
                  type="datetime-local"
                  value={toDraft}
                  onChange={(event) => {
                    setToDraft(event.target.value);
                    setFilterError(null);
                  }}
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
              {hasFilters && (
                <button className="button button-secondary" type="button" onClick={clearFilters}>
                  Скинути
                </button>
              )}
            </form>
            {filterError && (
              <div className="state-card state-error" role="alert">
                {filterError}
              </div>
            )}
          </>
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
