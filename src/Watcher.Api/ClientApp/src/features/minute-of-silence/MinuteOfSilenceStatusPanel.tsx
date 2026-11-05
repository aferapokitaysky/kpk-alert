import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getMinuteOfSilenceStatus,
  MinuteOfSilenceApiError,
  type MinuteOfSilenceStatus,
} from './api';
import type { SessionState } from '../../state';

type StatusError = 'unauthenticated' | 'forbidden' | 'unavailable';

const skipReasonLabels: Record<string, string> = {
  missed_window: 'Система не встигла обробити розклад у дозволене вікно запуску.',
  outside_execution_window: 'Перевірку виконано поза дозволеним часовим вікном.',
  preset_unavailable: 'Не знайдено опублікований сценарій хвилини мовчання.',
  preset_manifest_invalid: 'Маніфест сценарію хвилини мовчання має некоректний формат.',
  no_active_locations: 'Немає активних корпусів для запуску.',
};

function canViewStatus(session: SessionState): boolean {
  return session.status === 'authenticated' && session.capabilities.canViewAudit;
}

function getErrorState(error: unknown): StatusError {
  if (error instanceof MinuteOfSilenceApiError) {
    if (error.status === 401) return 'unauthenticated';
    if (error.status === 403) return 'forbidden';
  }
  return 'unavailable';
}

function errorLabel(error: StatusError): string {
  if (error === 'unauthenticated')
    return 'Сесія завершилася. Увійдіть знову, щоб переглянути статус.';
  if (error === 'forbidden') return 'Немає доступу до статусу хвилини мовчання.';
  return 'Не вдалося завантажити статус хвилини мовчання. Перевірте з’єднання та спробуйте ще раз.';
}

function formatScheduledAt(value: string): string {
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'long',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(new Date(value));
}

function getOutcome(status: MinuteOfSilenceStatus): { title: string; detail: string } {
  if (status.outcome === 'queued') {
    return {
      title: 'Команду поставлено в чергу.',
      detail: 'Це підтверджує створення команди, але не відтворення звуку на вузлах.',
    };
  }
  if (status.outcome === 'skipped') {
    return {
      title: 'Запуск пропущено.',
      detail: status.skipReason
        ? (skipReasonLabels[status.skipReason] ?? 'Причину пропуску не вдалося визначити.')
        : 'Причину пропуску не вдалося визначити.',
    };
  }
  return {
    title: 'Результат ще не зафіксовано.',
    detail: 'Запис про обробку розкладу за цю дату поки відсутній.',
  };
}

function StatusDetails({ status }: { status: MinuteOfSilenceStatus }) {
  const outcome = getOutcome(status);
  return (
    <div className="state-card minute-silence-result" role="status">
      <div>
        <strong>{outcome.title}</strong>
        <p>{outcome.detail}</p>
        <p>Розклад: {formatScheduledAt(status.scheduledAtUtc)} (Europe/Kyiv).</p>
        {status.processedAtUtc && (
          <p>Оброблено о {formatScheduledAt(status.processedAtUtc)} (Europe/Kyiv).</p>
        )}
      </div>
    </div>
  );
}

export function MinuteOfSilenceStatusPanel({ session }: { session: SessionState }) {
  const canView = canViewStatus(session);
  const [loading, setLoading] = useState(true);
  const [status, setStatus] = useState<MinuteOfSilenceStatus | null>(null);
  const [error, setError] = useState<StatusError | null>(null);
  const activeRequest = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    if (!canView || document.hidden || activeRequest.current) return;

    const controller = new AbortController();
    activeRequest.current = controller;
    setLoading(true);
    setError(null);

    try {
      const result = await getMinuteOfSilenceStatus(controller.signal);
      if (!controller.signal.aborted) setStatus(result);
    } catch (requestError) {
      if (!controller.signal.aborted) setError(getErrorState(requestError));
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

    const pausePolling = (updateLoadingState = false) => {
      if (timer !== null) {
        window.clearInterval(timer);
        timer = null;
      }
      activeRequest.current?.abort();
      activeRequest.current = null;
      if (updateLoadingState) setLoading(false);
    };

    const resumePolling = () => {
      if (document.hidden || timer !== null) return;
      void load();
      timer = window.setInterval(() => {
        if (!document.hidden) void load();
      }, 60_000);
    };

    const handleVisibilityChange = () => {
      if (document.hidden) pausePolling(true);
      else resumePolling();
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    resumePolling();

    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      pausePolling();
    };
  }, [canView, load]);

  if (!canView) return null;

  return (
    <section
      className="panel minute-silence-panel"
      aria-labelledby="minute-silence-title"
      aria-busy={loading}
    >
      <div className="panel-heading">
        <div>
          <p className="eyebrow">ЩОДЕННИЙ РОЗКЛАД</p>
          <h2 id="minute-silence-title">Хвилина мовчання</h2>
          <p>Щодня о 09:00 за київським часом (Europe/Kyiv).</p>
        </div>
        <button
          className="button button-secondary"
          type="button"
          aria-disabled={loading}
          onClick={() => {
            if (!loading) void load();
          }}
        >
          {loading ? 'Оновлення…' : error ? 'Повторити' : 'Оновити'}
        </button>
      </div>

      {loading && !status && (
        <p className="state-card" role="status">
          Завантажуємо статус хвилини мовчання…
        </p>
      )}
      {loading && status && (
        <p className="minute-silence-refreshing" role="status">
          Оновлюємо статус. Показано останню отриману відповідь.
        </p>
      )}
      <div className="minute-silence-body">
        {error && (
          <div className="state-card state-error minute-silence-error" role="alert">
            {errorLabel(error)}
          </div>
        )}
        {status && <StatusDetails status={status} />}
      </div>
    </section>
  );
}
