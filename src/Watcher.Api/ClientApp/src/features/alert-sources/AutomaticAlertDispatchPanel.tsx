import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '../../api';
import { getAutomaticAlertDispatchStatus, type AutomaticAlertDispatchStatus } from './api';

type PanelState =
  | { status: 'loading' }
  | { status: 'ready'; data: AutomaticAlertDispatchStatus }
  | { status: 'error'; message: string };

const reasonLabels: Record<string, string> = {
  awaiting_geographic_clear: 'Очікується підтвердження відбою для охопленої території.',
  alert_still_active: 'Тривога триває за актуальними даними.',
  awaiting_start_delivery: 'Очікується підготовка доставки команди запуску.',
  confirmed_inactive: 'Отримано свіже підтвердження відбою.',
  fallback_alert_active: 'Запуск підтверджено резервним джерелом.',
  invalid_open_episode: 'Стан епізоду некоректний. Потрібна перевірка адміністратора.',
  no_active_locations: 'Немає активних корпусів для доставки.',
  no_active_routable_deliveries: 'Активних доставок, які потрібно зупинити, немає.',
  no_fresh_observation: 'Немає свіжого підтвердженого спостереження.',
  no_open_alert: 'Активної тривоги немає.',
  observation_not_actionable: 'Дані неповні або неоднозначні; команда не створюється.',
  preset_manifest_invalid: 'Опублікований пресет має некоректний manifest.',
  preset_unavailable: 'Опублікований пресет повітряної тривоги не налаштований.',
  primary_alert_active: 'Запуск підтверджено UkraineAlarm.',
  tertiary_alert_active: 'Запуск підтверджено alerts.in.ua.',
};

function formatTime(value: string): string {
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(new Date(value));
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError && error.status === 401) return 'Сесію завершено. Увійдіть знову.';
  if (error instanceof ApiError && error.status === 403)
    return 'Стан автоматичного оповіщення доступний лише глобальному адміністратору або аудитору.';
  return 'Не вдалося завантажити стан автоматичного оповіщення.';
}

export function AutomaticAlertDispatchPanel() {
  const [state, setState] = useState<PanelState>({ status: 'loading' });
  const activeRequest = useRef<AbortController | null>(null);

  const load = useCallback(async () => {
    activeRequest.current?.abort();
    const controller = new AbortController();
    activeRequest.current = controller;
    try {
      const data = await getAutomaticAlertDispatchStatus(controller.signal);
      if (!controller.signal.aborted) setState({ status: 'ready', data });
    } catch (error) {
      if (!controller.signal.aborted) setState({ status: 'error', message: errorMessage(error) });
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, []);

  useEffect(() => {
    const initialLoad = window.setTimeout(() => void load(), 0);
    const timer = window.setInterval(() => void load(), 60_000);
    return () => {
      window.clearTimeout(initialLoad);
      activeRequest.current?.abort();
      activeRequest.current = null;
      window.clearInterval(timer);
    };
  }, [load]);

  return (
    <section
      className="alert-source-panel automatic-alert-panel"
      aria-labelledby="automatic-alert-title"
    >
      <header className="alert-source-panel-heading">
        <div>
          <p className="eyebrow">АВТОМАТИЧНЕ ОПОВІЩЕННЯ</p>
          <h2 id="automatic-alert-title">Повітряна тривога</h2>
        </div>
        <button
          className="button button-secondary"
          type="button"
          onClick={() => void load()}
          disabled={state.status === 'loading'}
        >
          {state.status === 'loading' ? 'Оновлення…' : 'Оновити'}
        </button>
      </header>

      {state.status === 'loading' && <p role="status">Завантажуємо стан автоматизації…</p>}
      {state.status === 'error' && (
        <div className="alert-source-message alert-source-message-error" role="alert">
          <span>{state.message}</span>
          <button className="inline-button" type="button" onClick={() => void load()}>
            Повторити
          </button>
        </div>
      )}
      {state.status === 'ready' && (
        <div className="automatic-alert-content">
          <p className="automatic-alert-state" role="status">
            <strong>Автоматизація:</strong>{' '}
            {state.data.enabled ? 'Увімкнена' : 'Вимкнена конфігурацією сервера'}
          </p>
          <p>
            <strong>Епізод:</strong>{' '}
            {state.data.state === 'alerting' ? 'Тривогу підтверджено' : 'Активного епізоду немає'}
          </p>
          {state.data.state === 'alerting' && state.data.startedAtUtc && (
            <p>
              <strong>Початок:</strong> {formatTime(state.data.startedAtUtc)} ·{' '}
              {state.data.scope ?? 'невідома територія'}
            </p>
          )}
          <p>{reasonLabels[state.data.lastDecisionCode] ?? 'Стан очікує повторної оцінки.'}</p>
          {state.data.startCommandId && (
            <p className="automatic-alert-command-id">
              <strong>Команда запуску:</strong> {state.data.startCommandId}
            </p>
          )}
          <p className="alert-source-observation-meta">
            Перевірено {formatTime(state.data.updatedAtUtc)}. Створення команди не є підтвердженням
            відтворення звуку.
          </p>
        </div>
      )}
    </section>
  );
}
