import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { pageCount, type SessionState } from '../../state';
import {
  getMinuteOfSilenceHistory,
  MinuteOfSilenceHistoryApiError,
  type MinuteOfSilenceHistoryPage,
} from './history-api';
import './minute-of-silence-history.css';

type HistoryState =
  | { status: 'loading' }
  | { status: 'ready'; data: MinuteOfSilenceHistoryPage }
  | { status: 'unauthenticated' }
  | { status: 'forbidden' }
  | { status: 'error' };

interface Props {
  session: SessionState;
  onSessionExpired: () => void;
  onSessionRetry: () => void;
}

const skipReasonLabels: Record<string, string> = {
  missed_window: 'Запуск не відбувся у дозволене часове вікно.',
  outside_execution_window: 'Перевірку виконано поза дозволеним часовим вікном.',
  preset_unavailable: 'Сценарій хвилини мовчання був недоступний.',
  preset_manifest_invalid: 'Маніфест сценарію мав некоректний формат.',
  no_active_locations: 'Не було активних корпусів для запуску.',
};

function kyivDateString(date = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-GB', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    timeZone: 'Europe/Kyiv',
  }).formatToParts(date);
  const part = (type: string) => parts.find((value) => value.type === type)?.value ?? '';
  return `${part('year')}-${part('month')}-${part('day')}`;
}

function offsetDate(date: string, days: number): string {
  const value = new Date(`${date}T00:00:00.000Z`);
  value.setUTCDate(value.getUTCDate() + days);
  return value.toISOString().slice(0, 10);
}

function formatKyivDate(value: string): string {
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeZone: 'Europe/Kyiv',
  }).format(new Date(`${value}T00:00:00Z`));
}

function formatKyivDateTime(value: string): string {
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(new Date(value));
}

function errorState(
  error: unknown,
): Exclude<HistoryState, { status: 'loading' | 'ready' }>['status'] {
  if (error instanceof MinuteOfSilenceHistoryApiError) {
    if (error.status === 401) return 'unauthenticated';
    if (error.status === 403) return 'forbidden';
  }
  return 'error';
}

export function MinuteOfSilenceHistoryPanel({ session, onSessionExpired, onSessionRetry }: Props) {
  const canView = session.status === 'authenticated' && session.capabilities.canViewAudit;
  const today = kyivDateString();
  const [period, setPeriod] = useState(() => {
    const currentKyivDate = kyivDateString();
    return { from: offsetDate(currentKyivDate, -29), to: currentKyivDate };
  });
  const [fromDraft, setFromDraft] = useState(period.from);
  const [toDraft, setToDraft] = useState(period.to);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [state, setState] = useState<HistoryState>({ status: 'loading' });
  const requestSequence = useRef(0);
  const activeRequest = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    if (!canView) return;
    const sequence = ++requestSequence.current;
    activeRequest.current?.abort();
    const controller = new AbortController();
    activeRequest.current = controller;
    setState({ status: 'loading' });
    try {
      const data = await getMinuteOfSilenceHistory(
        period.from,
        period.to,
        page,
        pageSize,
        controller.signal,
      );
      if (sequence !== requestSequence.current) return;
      const lastAvailablePage = pageCount(data.totalCount, pageSize);
      if (page > lastAvailablePage) {
        setPage(lastAvailablePage);
        return;
      }
      setState({ status: 'ready', data });
    } catch (error) {
      if (sequence !== requestSequence.current || controller.signal.aborted) return;
      const status = errorState(error);
      setState({ status });
      if (status === 'unauthenticated') onSessionExpired();
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, [canView, onSessionExpired, page, pageSize, period.from, period.to, setPage]);

  useEffect(() => {
    if (!canView) return;
    const timer = window.setTimeout(() => void load(), 0);
    return () => {
      window.clearTimeout(timer);
      requestSequence.current += 1;
      activeRequest.current?.abort();
      activeRequest.current = null;
    };
  }, [canView, load]);

  if (!canView) return null;

  const submitPeriod = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setPage(1);
    setPeriod({ from: fromDraft, to: toDraft });
  };
  const totalPages = state.status === 'ready' ? pageCount(state.data.totalCount, pageSize) : 1;
  const latestAllowedTo = fromDraft ? [today, offsetDate(fromDraft, 89)].sort()[0] : today;
  const earliestAllowedFrom = toDraft ? offsetDate(toDraft, -89) : offsetDate(today, -89);

  return (
    <section
      className="panel minute-history"
      aria-labelledby="minute-history-title"
      aria-busy={state.status === 'loading'}
    >
      <div className="minute-history__heading">
        <div>
          <p className="eyebrow">ХВИЛИНА МОВЧАННЯ</p>
          <h2 id="minute-history-title">Історія результатів</h2>
          <p>Збережені результати запуску о 09:00 за київським часом.</p>
        </div>
        {state.status === 'ready' && (
          <span className="selection-count">Записів: {state.data.totalCount}</span>
        )}
      </div>

      <form className="minute-history__filters" onSubmit={submitPeriod}>
        <label className="form-field">
          <span>Від дати</span>
          <input
            className="form-control"
            type="date"
            value={fromDraft}
            min={earliestAllowedFrom}
            max={toDraft || today}
            onChange={(event) => setFromDraft(event.target.value)}
            required
          />
        </label>
        <label className="form-field">
          <span>До дати</span>
          <input
            className="form-control"
            type="date"
            value={toDraft}
            min={fromDraft}
            max={latestAllowedTo}
            onChange={(event) => setToDraft(event.target.value)}
            required
          />
        </label>
        <button className="button button-secondary minute-history__submit" type="submit">
          Показати
        </button>
      </form>

      {state.status === 'loading' && (
        <div className="state-card" role="status">
          Завантажуємо історію…
        </div>
      )}
      {state.status === 'unauthenticated' && (
        <div className="state-card state-denied" role="alert">
          Сесію завершено.{' '}
          <button className="inline-button" type="button" onClick={onSessionRetry}>
            Увійти знову
          </button>
        </div>
      )}
      {state.status === 'forbidden' && (
        <div className="state-card state-denied" role="alert">
          Історія доступна лише адміністраторам і аудиторам.
        </div>
      )}
      {state.status === 'error' && (
        <div className="state-card state-error" role="alert">
          <span>Не вдалося завантажити історію.</span>
          <button className="inline-button" type="button" onClick={() => void load()}>
            Повторити
          </button>
        </div>
      )}
      {state.status === 'ready' && state.data.items.length === 0 && (
        <div className="minute-history__empty">
          {state.data.totalCount === 0
            ? 'За цей період збережених результатів немає.'
            : 'На цій сторінці результатів немає. Перейдіть на попередню сторінку.'}
        </div>
      )}
      {state.status === 'ready' && state.data.items.length > 0 && (
        <ol className="minute-history__list" aria-label="Результати хвилини мовчання">
          {state.data.items.map((item) => (
            <li className="minute-history__entry" key={item.runDate}>
              <div className="minute-history__entry-heading">
                <h3>{formatKyivDate(item.runDate)}</h3>
                <span
                  className={`minute-history__outcome minute-history__outcome--${item.outcome}`}
                >
                  {item.outcome === 'queued' ? 'Команду поставлено в чергу' : 'Запуск пропущено'}
                </span>
              </div>
              {item.skipReason && (
                <p>{skipReasonLabels[item.skipReason] ?? `Причина: ${item.skipReason}`}</p>
              )}
              <p className="minute-history__time">
                Оброблено {formatKyivDateTime(item.processedAtUtc)} (Europe/Kyiv)
              </p>
            </li>
          ))}
        </ol>
      )}
      {state.status === 'ready' && totalPages > 1 && (
        <nav className="minute-history__pagination" aria-label="Сторінки історії">
          <label className="form-field minute-history__page-size">
            <span>На сторінці</span>
            <select
              className="form-control form-control-sm"
              value={pageSize}
              onChange={(event) => {
                setPageSize(Number(event.target.value));
                setPage(1);
              }}
            >
              <option value={10}>10</option>
              <option value={25}>25</option>
              <option value={50}>50</option>
            </select>
          </label>
          <span aria-live="polite">
            Сторінка {page} з {totalPages}
          </span>
          <button
            className="button button-secondary"
            type="button"
            disabled={page <= 1}
            onClick={() => setPage((current) => current - 1)}
          >
            Назад
          </button>
          <button
            className="button button-secondary"
            type="button"
            disabled={page >= totalPages}
            onClick={() => setPage((current) => current + 1)}
          >
            Далі
          </button>
        </nav>
      )}
    </section>
  );
}
