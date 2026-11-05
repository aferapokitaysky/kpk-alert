import { useCallback, useEffect, useRef, useState } from 'react';
import {
  AlertSourceApiError,
  getAlertSourcesStatus,
  type AlertLevel,
  type AlertSourceObservation,
  type AlertSourcesStatus,
  type AlertTargetStatus,
} from './api';

type PanelState =
  | { status: 'loading' }
  | { status: 'ready'; data: AlertSourcesStatus }
  | { status: 'refreshing'; data: AlertSourcesStatus }
  | { status: 'error'; message: string };

function statusLabel(level: AlertLevel, alertLevel: 'red' | 'yellow' | null): string {
  if (level === 'active' && alertLevel === 'red') return 'Тривога активна · червоний рівень';
  if (level === 'active' && alertLevel === 'yellow') return 'Попередження · жовтий рівень';
  if (level === 'active') return 'Активний статус · рівень не уточнено';
  if (level === 'inactive') return 'Тривоги немає';
  if (level === 'partial') return 'Часткова тривога';
  if (level === 'conflict') return 'Джерела повідомляють різне';
  return 'Статус не підтверджено';
}

function freshnessLabel(observation: AlertSourceObservation): string {
  if (observation.freshness === 'fresh') return 'Актуальні дані';
  if (observation.freshness === 'stale') return 'Дані застаріли';
  return 'Немає підтверджених даних';
}

function targetFreshnessLabel(freshness: AlertTargetStatus['freshness']): string {
  if (freshness === 'fresh') return 'Актуально';
  if (freshness === 'stale') return 'Застаріло';
  return 'Немає даних';
}

function sourceErrorLabel(code: string | null): string | null {
  if (code === 'not_configured') return 'Джерело не налаштовано на сервері.';
  if (code === 'rate_limited') return 'Джерело тимчасово обмежило запити.';
  if (code === 'invalid_response') return 'Джерело повернуло дані невідомого формату.';
  if (code === 'source_unavailable') return 'Джерело тимчасово недоступне.';
  return null;
}

function formatTime(value: string | null): string {
  if (value === null) return 'Час не повідомлено';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 'Час не повідомлено';
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(date);
}

function Observation({ observation }: { observation: AlertSourceObservation }) {
  const lastKnownStatus = observation.lastKnownLevel
    ? statusLabel(observation.lastKnownLevel, observation.lastKnownAlertLevel)
    : null;
  const errorMessage = sourceErrorLabel(observation.errorCode);

  return (
    <li className={`alert-source-observation freshness-${observation.freshness}`}>
      <div className="alert-source-observation-heading">
        <strong>{observation.sourceName}</strong>
        <span>{observation.scopeName}</span>
      </div>
      <p className="alert-source-observation-state">
        {observation.freshness === 'stale'
          ? 'Поточний статус невідомий'
          : statusLabel(observation.level, observation.alertLevel)}
      </p>
      <p className="alert-source-observation-meta">
        {freshnessLabel(observation)} · Перевірено {formatTime(observation.checkedAtUtc)}
      </p>
      {observation.dataUpdatedAtUtc && (
        <p className="alert-source-observation-meta">
          Дані змінено {formatTime(observation.dataUpdatedAtUtc)}
        </p>
      )}
      {lastKnownStatus && observation.freshness === 'stale' && (
        <p className="alert-source-last-known">Останній відомий статус: {lastKnownStatus}.</p>
      )}
      {errorMessage && <p className="alert-source-error-detail">{errorMessage}</p>}
      <a href={observation.attributionUrl} target="_blank" rel="noreferrer">
        Дані: {observation.sourceName}
      </a>
    </li>
  );
}

function Target({ target }: { target: AlertTargetStatus }) {
  const selected = target.authoritativeObservation;
  return (
    <article className={`alert-source-target level-${target.level}`}>
      <div className="alert-source-target-heading">
        <div>
          <p className="alert-source-target-scope">{target.scopeName}</p>
          <h3>{statusLabel(target.level, target.alertLevel)}</h3>
        </div>
        <span className={`alert-source-badge level-${target.level}`}>
          {targetFreshnessLabel(target.freshness)}
        </span>
      </div>
      {selected && (
        <p className="alert-source-selected">
          {selected.freshness === 'fresh'
            ? 'Основне джерело для цього рівня:'
            : 'Останнє джерело для цього рівня:'}{' '}
          {selected.sourceName}. Перевірено {formatTime(selected.checkedAtUtc)}.
        </p>
      )}
      {target.observations.length > 0 ? (
        <ul className="alert-source-observations">
          {target.observations.map((observation) => (
            <Observation
              key={`${observation.source}:${observation.scope}`}
              observation={observation}
            />
          ))}
        </ul>
      ) : (
        <p className="alert-source-empty">Для цього рівня немає джерел зі статусом.</p>
      )}
    </article>
  );
}

function errorMessage(error: unknown): string {
  if (error instanceof AlertSourceApiError && error.status === 429) {
    return 'Сервер тимчасово обмежив оновлення статусу. Спробуйте пізніше.';
  }
  if (error instanceof AlertSourceApiError && error.status >= 500) {
    return 'Сервер статусу тривог тимчасово недоступний.';
  }
  return 'Не вдалося отримати статус тривог. Перевірте з’єднання та повторіть спробу.';
}

export function AlertSourcesPanel() {
  const [state, setState] = useState<PanelState>({ status: 'loading' });
  const activeRequest = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    activeRequest.current?.abort();
    const controller = new AbortController();
    activeRequest.current = controller;
    setState((current) =>
      current.status === 'ready' || current.status === 'refreshing'
        ? { status: 'refreshing', data: current.data }
        : { status: 'loading' },
    );
    try {
      const data = await getAlertSourcesStatus(controller.signal);
      if (!controller.signal.aborted) setState({ status: 'ready', data });
    } catch (error) {
      if (!controller.signal.aborted) setState({ status: 'error', message: errorMessage(error) });
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, []);

  useEffect(() => {
    let timer: number | null = null;

    const pausePolling = () => {
      if (timer !== null) {
        window.clearInterval(timer);
        timer = null;
      }
      activeRequest.current?.abort();
    };

    const resumePolling = () => {
      if (document.hidden || timer !== null) return;

      void load();
      timer = window.setInterval(() => {
        if (!document.hidden) void load();
      }, 60_000);
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

  const data = state.status === 'ready' || state.status === 'refreshing' ? state.data : null;

  return (
    <section className="alert-source-panel" aria-labelledby="alert-source-title">
      <header className="alert-source-panel-heading">
        <div>
          <p className="eyebrow">ДОДАТКОВІ ДАНІ</p>
          <h2 id="alert-source-title">Повітряні тривоги</h2>
          <p>Статуси наведено окремо за областю, районом і громадою.</p>
        </div>
        <button
          className="button button-secondary"
          type="button"
          onClick={() => void load()}
          disabled={state.status === 'loading' || state.status === 'refreshing'}
        >
          {state.status === 'loading' || state.status === 'refreshing' ? 'Оновлення…' : 'Оновити'}
        </button>
      </header>

      {state.status === 'loading' && (
        <p className="alert-source-message" role="status">
          Завантажуємо статуси джерел…
        </p>
      )}
      {state.status === 'error' && (
        <div className="alert-source-message alert-source-message-error" role="alert">
          <span>{state.message}</span>
          <button className="inline-button" type="button" onClick={() => void load()}>
            Повторити
          </button>
        </div>
      )}
      {data && (
        <>
          {state.status === 'refreshing' && (
            <p className="alert-source-refreshing" role="status">
              Оновлюємо дані. Показано останню відповідь сервера.
            </p>
          )}
          <div className="alert-source-targets">
            {data.targets.map((target) => (
              <Target key={`${target.scope}:${target.scopeId}`} target={target} />
            ))}
          </div>
          <p className="alert-source-disclaimer">
            Дані мають інформаційний характер і можуть надходити із затримкою. Вони не замінюють
            офіційні сигнали оповіщення.
          </p>
        </>
      )}
    </section>
  );
}
