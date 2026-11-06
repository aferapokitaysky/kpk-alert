import { useCallback, useEffect, useRef, useState } from 'react';
import {
  getUkraineAlarmWebhookStatus,
  reconcileUkraineAlarmWebhookState,
  startUkraineAlarmWebhookOperation,
  UkraineAlarmWebhookApiError,
  resolveUkraineAlarmWebhookOperation,
  type UkraineAlarmWebhookAction,
  type UkraineAlarmWebhookOperation,
  type UkraineAlarmWebhookRegistrationStatus,
} from './api';

type PanelState =
  | { status: 'loading' }
  | { status: 'ready'; data: UkraineAlarmWebhookRegistrationStatus }
  | { status: 'refreshing'; data: UkraineAlarmWebhookRegistrationStatus }
  | { status: 'error'; message: string };

type PendingAction = UkraineAlarmWebhookAction | 'reconcile' | 'resolve' | null;

function registrationStateLabel(state: UkraineAlarmWebhookRegistrationStatus['state']): string {
  if (state === 'registered') return 'Webhook зареєстрований';
  if (state === 'not_registered') return 'Webhook не зареєстрований';
  return 'Стан потрібно звірити';
}

function operationStateLabel(state: UkraineAlarmWebhookOperation['state']): string {
  if (state === 'Pending') return 'Операція очікує виконання';
  if (state === 'Running') return 'Провайдер обробляє запит';
  if (state === 'Unknown') return 'Результат невідомий — звірте стан у кабінеті';
  if (state === 'Succeeded') return 'Провайдер прийняв запит';
  if (state === 'Rejected') return 'Провайдер відхилив запит';
  return 'Операцію звірено';
}

function errorMessage(error: unknown): string {
  if (error instanceof UkraineAlarmWebhookApiError) {
    if (error.status === 401) return 'Сесію завершено. Увійдіть знову.';
    if (error.status === 403) return 'Керувати webhook може лише глобальний адміністратор.';
    if (error.status === 409)
      return 'Стан змінився або інша операція ще виконується. Оновіть дані.';
    if (error.status === 503)
      return 'Перевірте токен UkraineAlarm і публічну HTTPS-адресу сервера.';
    if (error.status === 502)
      return 'Провайдер відхилив запит. Перевірте стан webhook перед повтором.';
  }
  return 'Не вдалося оновити стан webhook. Перевірте з’єднання та повторіть спробу.';
}

function formatTime(value: string): string {
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(new Date(value));
}

export function UkraineAlarmWebhookPanel() {
  const [state, setState] = useState<PanelState>({ status: 'loading' });
  const [pendingAction, setPendingAction] = useState<PendingAction>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [confirmedRegistration, setConfirmedRegistration] = useState<boolean | null>(null);
  const activeRequest = useRef<AbortController | null>(null);
  const actionLock = useRef(false);
  const retryKey = useRef<{ action: UkraineAlarmWebhookAction; key: string } | null>(null);

  const load = useCallback(async (force = false) => {
    if (document.hidden || (activeRequest.current && !force)) return;

    activeRequest.current?.abort();

    const controller = new AbortController();
    activeRequest.current = controller;
    setState((current) =>
      current.status === 'ready' || current.status === 'refreshing'
        ? { status: 'refreshing', data: current.data }
        : { status: 'loading' },
    );

    try {
      const data = await getUkraineAlarmWebhookStatus(controller.signal);
      if (!controller.signal.aborted) {
        if (
          data.activeOperation === null ||
          retryKey.current?.action === data.activeOperation.action
        ) {
          retryKey.current = null;
        }
        setState({ status: 'ready', data });
      }
    } catch (error) {
      if (!controller.signal.aborted) setState({ status: 'error', message: errorMessage(error) });
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, []);

  useEffect(() => {
    let timer: number | null = null;

    const resumePolling = () => {
      if (document.hidden || timer !== null) return;
      void load();
      timer = window.setInterval(() => {
        if (!document.hidden) void load();
      }, 15_000);
    };

    const pausePolling = () => {
      if (timer !== null) {
        window.clearInterval(timer);
        timer = null;
      }
      activeRequest.current?.abort();
      activeRequest.current = null;
    };

    const refreshWhenVisible = () => {
      if (document.hidden) {
        pausePolling();
      } else {
        resumePolling();
      }
    };

    document.addEventListener('visibilitychange', refreshWhenVisible);
    resumePolling();

    return () => {
      document.removeEventListener('visibilitychange', refreshWhenVisible);
      pausePolling();
    };
  }, [load]);

  const runAction = async (action: UkraineAlarmWebhookAction) => {
    if (actionLock.current) return;

    let key = retryKey.current;
    if (!key || key.action !== action) {
      try {
        key = { action, key: crypto.randomUUID() };
      } catch {
        setActionError('Браузер не може створити ключ безпечного повтору. Оновіть сторінку.');
        return;
      }
      retryKey.current = key;
    }

    actionLock.current = true;
    setPendingAction(action);
    setActionError(null);
    try {
      await startUkraineAlarmWebhookOperation(action, key.key);
      retryKey.current = null;
      setConfirmedRegistration(null);
      await load(true);
    } catch (error) {
      if (error instanceof UkraineAlarmWebhookApiError) retryKey.current = null;
      setActionError(errorMessage(error));
      await load(true);
    } finally {
      actionLock.current = false;
      setPendingAction(null);
    }
  };

  const confirmRegistrationState = async (isRegistered: boolean, resolve: boolean) => {
    if (actionLock.current || state.status !== 'ready') return;
    actionLock.current = true;
    setPendingAction(resolve ? 'resolve' : 'reconcile');
    setActionError(null);
    try {
      if (resolve) {
        const operation = state.data.activeOperation;
        if (!operation || operation.state !== 'Unknown') return;
        await resolveUkraineAlarmWebhookOperation(operation.id, isRegistered);
      } else {
        await reconcileUkraineAlarmWebhookState(isRegistered);
      }
      setConfirmedRegistration(null);
      await load(true);
    } catch (error) {
      setActionError(errorMessage(error));
      await load(true);
    } finally {
      actionLock.current = false;
      setPendingAction(null);
    }
  };

  const data = state.status === 'ready' || state.status === 'refreshing' ? state.data : null;
  const activeOperation = data?.activeOperation ?? null;
  const canReconcile = data?.state === 'unknown' && activeOperation === null;
  const canResolveUnknown = activeOperation?.state === 'Unknown';
  const isBusy = pendingAction !== null;

  return (
    <section
      className="alert-source-panel webhook-management-panel"
      aria-labelledby="webhook-title"
    >
      <header className="alert-source-panel-heading">
        <div>
          <p className="eyebrow">ІНТЕГРАЦІЯ · АДМІНІСТРУВАННЯ</p>
          <h2 id="webhook-title">Webhook UkraineAlarm</h2>
          <p>Стан реєстрації та останньої незавершеної операції.</p>
        </div>
        <button
          className="button button-secondary"
          type="button"
          onClick={() => void load()}
          disabled={state.status === 'loading' || state.status === 'refreshing' || isBusy}
        >
          {state.status === 'loading' || state.status === 'refreshing' ? 'Оновлення…' : 'Оновити'}
        </button>
      </header>

      {state.status === 'loading' && (
        <p className="alert-source-message" role="status">
          Завантажуємо стан webhook…
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
        <div className="webhook-management-content">
          <p className="webhook-registration-state" role="status">
            <strong>Стан:</strong> {registrationStateLabel(data.state)}
            <span> · Оновлено {formatTime(data.updatedAtUtc)}</span>
          </p>

          {activeOperation && (
            <div className="alert-source-message" role="status" aria-live="polite">
              <strong>{operationStateLabel(activeOperation.state)}</strong>
              <span>
                {activeOperation.action === 'register' ? 'Реєстрація' : 'Оновлення'} ·{' '}
                {formatTime(activeOperation.updatedAtUtc)}
              </span>
              <a href={activeOperation.callbackUrl} target="_blank" rel="noreferrer">
                Callback URL
              </a>
            </div>
          )}

          {activeOperation && ['Pending', 'Running'].includes(activeOperation.state) && (
            <p className="alert-source-empty">
              Дочекайтеся завершення операції. Повторний запит зараз заблокований сервером.
            </p>
          )}

          {activeOperation?.state === 'Unknown' && (
            <p className="alert-source-empty">
              Перевірте реєстрацію в кабінеті UkraineAlarm, потім підтвердьте фактичний стан нижче.
            </p>
          )}

          {(canReconcile || canResolveUnknown) && (
            <fieldset className="webhook-state-choice" disabled={isBusy}>
              <legend>
                {canResolveUnknown
                  ? 'Фактичний стан цієї операції в кабінеті UkraineAlarm'
                  : 'Поточний стан у кабінеті UkraineAlarm'}
              </legend>
              <label>
                <input
                  type="radio"
                  name="ukrainealarm-webhook-registered"
                  checked={confirmedRegistration === true}
                  onChange={() => setConfirmedRegistration(true)}
                />
                Зареєстрований
              </label>
              <label>
                <input
                  type="radio"
                  name="ukrainealarm-webhook-registered"
                  checked={confirmedRegistration === false}
                  onChange={() => setConfirmedRegistration(false)}
                />
                Не зареєстрований
              </label>
              <button
                className="button button-secondary"
                type="button"
                disabled={confirmedRegistration === null || isBusy}
                onClick={() =>
                  confirmedRegistration !== null &&
                  void confirmRegistrationState(confirmedRegistration, canResolveUnknown)
                }
              >
                {isBusy
                  ? 'Збереження…'
                  : canResolveUnknown
                    ? 'Підтвердити результат операції'
                    : 'Звірити стан реєстрації'}
              </button>
            </fieldset>
          )}

          {data.state === 'not_registered' && activeOperation === null && (
            <div className="webhook-management-actions">
              <p>Публічна адреса callback визначається конфігурацією сервера.</p>
              <button
                className="button button-primary"
                type="button"
                disabled={isBusy}
                onClick={() => void runAction('register')}
              >
                {pendingAction === 'register' ? 'Надсилання…' : 'Зареєструвати webhook'}
              </button>
            </div>
          )}

          {data.state === 'registered' && activeOperation === null && (
            <div className="webhook-management-actions">
              <p>Оновіть адресу, якщо змінилася публічна URL-адреса сервера.</p>
              <button
                className="button button-secondary"
                type="button"
                disabled={isBusy}
                onClick={() => void runAction('update')}
              >
                {pendingAction === 'update' ? 'Надсилання…' : 'Оновити адресу webhook'}
              </button>
            </div>
          )}
        </div>
      )}

      {actionError && (
        <p className="alert alert-error webhook-management-error" role="alert">
          {actionError}
        </p>
      )}
    </section>
  );
}
