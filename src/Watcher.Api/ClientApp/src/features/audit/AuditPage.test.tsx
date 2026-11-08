import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuditPage } from './AuditPage';

const auditSession = {
  status: 'authenticated' as const,
  roles: ['auditor'],
  capabilities: { canOperate: false, canViewAudit: true },
};

const minuteStatus = {
  runDate: '2026-11-06',
  timeZone: 'Europe/Kyiv',
  scheduledAtUtc: '2026-11-06T07:00:00Z',
  outcome: null,
  skipReason: null,
  commandId: null,
  processedAtUtc: null,
};

const minuteHistory = {
  items: [],
  from: '2026-10-08',
  to: '2026-11-06',
  page: 1,
  pageSize: 25,
  totalCount: 0,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('сторінка аудиту', () => {
  it('показує зрозумілі події та не виводить довільний вміст payload', async () => {
    const commandId = 'c6b2f302-0675-4a4d-88eb-cf18c9022920';
    const targetUserId = 'a0b1c2d3-e4f5-4678-9012-345678901234';
    const events = [
      {
        id: '1',
        eventType: 'alert_command.created',
        actorType: 'user',
        actorId: targetUserId,
        occurredAtUtc: '2026-11-06T07:00:00Z',
        payload: {
          CommandId: commandId,
          Action: 'start',
          PresetCode: 'alarm',
          AlertLevel: 'red',
          Source: 'UkraineAlarm',
          Scope: 'oblast',
          ScopeId: 'UA-53',
          CheckedAtUtc: '2026-11-06T07:00:00Z',
          secret: 'hidden-token',
        },
      },
      {
        id: '2',
        eventType: 'alert_command.stop_requested',
        actorType: 'user',
        actorId: targetUserId,
        occurredAtUtc: '2026-11-06T07:01:00Z',
        payload: { TargetCommandId: commandId },
      },
      {
        id: '3',
        eventType: 'alert_command.receipt_received',
        actorType: 'device',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:02:00Z',
        payload: {
          CommandId: commandId,
          DeliveryId: 'd6b2f302-0675-4a4d-88eb-cf18c9022920',
          State: 'Playing',
          DeliveryStateBefore: 'InFlight',
          DeliveryStateAfter: 'Playing',
        },
      },
      {
        id: '4',
        eventType: 'alert_command.delivery_retry_scheduled',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:03:00Z',
        payload: {
          CommandId: commandId,
          AttemptCount: 2,
          ReasonCode: 'presence_session_changed',
        },
      },
      {
        id: '5',
        eventType: 'auth.login.rejected',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:04:00Z',
        payload: { method: 'google', reason: 'account_not_allowed', email: 'private@example.com' },
      },
      {
        id: '6',
        eventType: 'user.role_granted',
        actorType: 'user',
        actorId: targetUserId,
        occurredAtUtc: '2026-11-06T07:05:00Z',
        payload: { targetUserId, roleCode: 'admin', token: 'hidden-secret' },
      },
      {
        id: '7',
        eventType: 'future.private_event',
        actorType: 'vendor_unknown',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:06:00Z',
        payload: { secret: 'must-not-render', callbackUrl: 'https://private.example' },
      },
      {
        id: '8',
        eventType: 'alert_command.delivery_attempt_started',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:07:00Z',
        payload: { CommandId: commandId, AttemptCount: 3, Action: 'start', PresetCode: 'alarm' },
      },
      {
        id: '9',
        eventType: 'alert_command.delivery_write_result',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:08:00Z',
        payload: {
          CommandId: commandId,
          Outcome: 'completed',
          ReasonCode: 'socket_write_completed',
        },
      },
      {
        id: '10',
        eventType: 'alert_command.device_playback_state_reported',
        actorType: 'device',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:09:00Z',
        payload: { CommandId: commandId, State: 'Playing' },
      },
      {
        id: '11',
        eventType: 'device.session_connected',
        actorType: 'device',
        actorId: targetUserId,
        occurredAtUtc: '2026-11-06T07:10:00Z',
        payload: { Id: commandId, DeviceId: targetUserId, LocationId: targetUserId },
      },
      {
        id: '12',
        eventType: 'automatic_alert.held',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:11:00Z',
        payload: {
          DecisionCode: 'no_fresh_observation',
          Observations: [
            {
              Source: 'UkraineAlarm',
              Scope: 'hromada',
              ScopeId: 'UA-53',
              Level: 'active',
              AlertLevel: 'red',
              Freshness: 'stale',
              CheckedAtUtc: '2026-11-06T07:11:00Z',
              DataUpdatedAtUtc: '2026-11-06T07:00:00Z',
              AccessToken: 'must-not-render',
            },
            {
              Source: 'alerts.in.ua',
              Scope: 'raion',
              ScopeId: 'UA-53',
              Level: 'unknown',
              Freshness: 'fresh',
            },
          ],
          ObservationsTruncated: true,
        },
      },
      {
        id: '13',
        eventType: 'integration.ukrainealarm_webhook.running',
        actorType: 'integration',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:12:00Z',
        payload: {
          OperationId: commandId,
          Action: 'unsubscribe',
          Outcome: 'provider_request_started',
          CallbackUrl: 'https://callback.private.example/path?token=private',
        },
      },
      {
        id: '14',
        eventType: 'alert_command.delivery_write_result',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:13:00Z',
        payload: { CommandId: commandId, Outcome: 'failed', ReasonCode: 'send_timeout' },
      },
      {
        id: '15',
        eventType: 'alert_command.delivery_write_result',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:14:00Z',
        payload: {
          CommandId: commandId,
          Outcome: 'not_started',
          ReasonCode: 'presence_session_changed',
        },
      },
      {
        id: '16',
        eventType: 'alert_command.delivery_write_result',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:15:00Z',
        payload: {
          CommandId: commandId,
          Outcome: 'indeterminate',
          ReasonCode: 'socket_write_result_unknown',
        },
      },
      {
        id: '17',
        eventType: 'alert_command.delivery_write_result',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:16:00Z',
        payload: {
          CommandId: commandId,
          Outcome: 'indeterminate',
          ReasonCode: 'socket_write_timed_out_result_unknown',
        },
      },
      {
        id: '18',
        eventType: 'alert_command.created',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:00:00Z',
        payload: {
          Action: 'start',
          PresetCode: 'minute_of_silence',
          ScheduledDate: '2026-11-06',
          ScheduledLocalTime: '09:00',
          ScheduledAtUtc: '2026-11-06T07:00:00Z',
          ScheduleTimeZoneId: 'Europe/Kyiv',
        },
      },
      {
        id: '19',
        eventType: 'minute_of_silence.skipped',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:01:00Z',
        payload: {
          RunDate: '2026-11-06',
          ScheduledLocalTime: '09:00',
          ScheduledAtUtc: '2026-11-06T07:00:00Z',
          ScheduleTimeZoneId: 'Europe/Kyiv',
          Reason: 'preset_unavailable',
        },
      },
      {
        id: '20',
        eventType: 'alert_command.delivery_expired',
        actorType: 'system',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:17:00Z',
        payload: {
          CommandId: commandId,
          DeliveryId: 'e6b2f302-0675-4a4d-88eb-cf18c9022920',
          AttemptCount: 2,
          StateBefore: 'InFlight',
          StateAfter: 'Expired',
          Reason: 'command_expired',
        },
      },
      {
        id: '21',
        eventType: 'alert_command.delivery_stopped',
        actorType: 'device',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:18:00Z',
        payload: {
          CommandId: commandId,
          DeliveryId: 'e6b2f302-0675-4a4d-88eb-cf18c9022920',
          StopCommandId: 'f6b2f302-0675-4a4d-88eb-cf18c9022920',
          StopDeliveryId: 'a6b2f302-0675-4a4d-88eb-cf18c9022920',
          DeliveryStateBefore: 'Playing',
          DeliveryStateAfter: 'Stopped',
        },
      },
      {
        id: '22',
        eventType: 'integration.ukrainealarm_webhook.rejected',
        actorType: 'user',
        actorId: targetUserId,
        occurredAtUtc: '2026-11-06T07:19:00Z',
        payload: {
          OperationId: 'b6b2f302-0675-4a4d-88eb-cf18c9022920',
          Action: 'subscribe',
          Outcome: 'provider_rejected',
        },
      },
      {
        id: '23',
        eventType: 'integration.ukrainealarm_webhook.state_reconciled',
        actorType: 'user',
        actorId: targetUserId,
        occurredAtUtc: '2026-11-06T07:20:00Z',
        payload: {
          IntegrationCode: 'ukrainealarm',
          IsRegistered: false,
          Outcome: 'operator_confirmed',
        },
      },
      {
        id: '24',
        eventType: 'integration.alert_source.state_changed',
        actorType: 'integration',
        actorId: null,
        occurredAtUtc: '2026-11-06T07:21:00Z',
        payload: {
          Source: 'neptun',
          Scope: 'oblast',
          ScopeId: '19',
          Level: 'inactive',
          Freshness: 'fresh',
          PreviousFreshness: 'stale',
          PreviousErrorCode: 'request_failed',
          LastKnownLevel: 'active',
          LastKnownAlertLevel: 'red',
          CheckedAtUtc: '2026-11-06T07:21:00Z',
          DataUpdatedAtUtc: '2026-11-06T07:20:00Z',
          ChangeType: 'changed',
          PreviousLevel: 'active',
          PreviousAlertLevel: 'red',
          PrivateUrl: 'https://private.example/token',
        },
      },
    ];
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      const path = String(input);
      const body = path.includes('/minute-of-silence/status/today')
        ? minuteStatus
        : path.includes('/minute-of-silence/history')
          ? minuteHistory
          : { items: events, page: 1, pageSize: 25, totalCount: events.length };
      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <AuditPage session={auditSession} onSessionExpired={vi.fn()} onSessionRetry={vi.fn()} />,
    );

    expect(await screen.findAllByRole('heading', { name: 'Створено команду' })).toHaveLength(2);
    expect(screen.getByRole('heading', { name: 'Запитано зупинку команди' })).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Отримано підтвердження від вузла' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Термін доставки минув' })).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Вузол повідомив про зупинку' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Запит вебхука відхилено' })).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Оновлено стан підписки на вебхук' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Змінено стан джерела тривоги' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Вхід відхилено' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Надано роль' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Подія системи' })).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Розпочато спробу надсилання команди' }),
    ).toBeInTheDocument();
    expect(
      screen.getAllByRole('heading', { name: 'Результат запису команди у WebSocket' }),
    ).toHaveLength(5);
    expect(
      screen.getByRole('heading', { name: 'Вузол повідомив стан відтворення' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Підключено сесію вузла' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Автоматичну дію відкладено' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Запит вебхука обробляється' })).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Пропущено запуск хвилини мовчання' }),
    ).toBeInTheDocument();
    for (const summary of screen.getAllByText('Деталі події')) fireEvent.click(summary);
    expect(screen.getAllByText(`Дія: запуск сигналу`)).toHaveLength(3);
    expect(screen.getAllByText('Місцевий час запуску: 09:00')).toHaveLength(2);
    expect(screen.getAllByText('Часовий пояс: Київ')).toHaveLength(2);
    expect(screen.getAllByText('Дата розкладу: 2026-11-06')).toHaveLength(2);
    expect(
      screen.getByText('Рівень спостереження джерела: червоний (не гучність)'),
    ).toBeInTheDocument();
    expect(screen.getByText('Джерело спостереження: UkraineAlarm')).toBeInTheDocument();
    expect(screen.getByText('Область спостереження: область')).toBeInTheDocument();
    expect(screen.getByText('Код області спостереження: UA-53')).toBeInTheDocument();
    expect(screen.getByText(`Зупиняє команду: ${commandId}`)).toBeInTheDocument();
    expect(
      screen.getByText('Підтвердження вузла: запущено програмний шлях відтворення'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Стан доставки: надсилається → запущено програмний шлях відтворення'),
    ).toBeInTheDocument();
    expect(screen.getByText('Стан доставки: надсилається → строк дії минув')).toBeInTheDocument();
    expect(screen.getByText('Причина: строк дії команди минув')).toBeInTheDocument();
    expect(
      screen.getByText('Команда зупинки: f6b2f302-0675-4a4d-88eb-cf18c9022920'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Доставка команди зупинки: a6b2f302-0675-4a4d-88eb-cf18c9022920'),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Причина: сесія вузла змінилася')).toHaveLength(2);
    expect(screen.getByText('Спосіб: Google')).toBeInTheDocument();
    expect(screen.getByText('Роль: admin')).toBeInTheDocument();
    expect(screen.getByText('future.private_event')).toBeInTheDocument();
    expect(screen.getByText('Спроба надсилання: 3')).toBeInTheDocument();
    expect(
      screen.getByText(/Запис команди у WebSocket завершився; отримання вузлом не підтверджене/),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Спроба запису не завершилася успішно; перевірте квитанцію вузла.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Запис команди не розпочався.')).toBeInTheDocument();
    expect(screen.getByText('Причина: вичерпано час очікування запису')).toBeInTheDocument();
    expect(screen.getAllByText('Причина: сесія вузла змінилася')).toHaveLength(2);
    expect(
      screen.getAllByText('Результат передавання невідомий; перевірте квитанцію вузла.'),
    ).toHaveLength(2);
    expect(
      screen.getByText('Причина: виклик запису не повернув підтвердженого результату'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Причина: час очікування запису минув без підтвердженого результату'),
    ).toBeInTheDocument();
    expect(screen.getByText(/звук із динаміка не вимірювався/)).toBeInTheDocument();
    expect(screen.getByText('Рішення: немає свіжого спостереження')).toBeInTheDocument();
    expect(
      screen.getByText(
        'Спостереження 1: UkraineAlarm · громада · код UA-53 · тривога активна · рівень API: червоний (не гучність) · застарілі дані',
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        'Спостереження 2: Alerts in UA · район · код UA-53 · невідомий стан · свіжі дані',
      ),
    ).toBeInTheDocument();
    expect(screen.getByText('Частину спостережень не показано.')).toBeInTheDocument();
    expect(screen.getAllByText('Зовнішня інтеграція · системна дія')).toHaveLength(2);
    expect(screen.getByText('Дія: скасування підписки')).toBeInTheDocument();
    expect(screen.getByText('Результат: запит надіслано провайдеру')).toBeInTheDocument();
    expect(screen.getByText('Результат: провайдер відхилив запит')).toBeInTheDocument();
    expect(screen.getByText('Стан підписки: не зареєстрована')).toBeInTheDocument();
    expect(screen.getByText('Результат: адміністратор звірив стан підписки')).toBeInTheDocument();
    expect(screen.getByText('Джерело: NEPTUN')).toBeInTheDocument();
    expect(screen.getByText('Стан джерела: стан неактивний')).toBeInTheDocument();
    expect(screen.getByText('Попередній стан: тривога активна')).toBeInTheDocument();
    expect(screen.getByText('Попередній рівень API: червоний (не гучність)')).toBeInTheDocument();
    expect(screen.getByText('Попередня актуальність: застарілі дані')).toBeInTheDocument();
    expect(screen.getByText('Попередня причина: запит до джерела не виконано')).toBeInTheDocument();
    expect(screen.getByText('Останній відомий стан: тривога активна')).toBeInTheDocument();
    expect(
      screen.getByText('Останній відомий рівень API: червоний (не гучність)'),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Інший суб’єкт · системна дія')).toHaveLength(1);
    expect(screen.getAllByText('Система · системна дія').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Вузол · системна дія').length).toBeGreaterThan(0);
    expect(
      screen.queryByText(
        /hidden-token|hidden-secret|private@example|must-not-render|private\.example|callback\.private|token=private|must-not-render/,
      ),
    ).not.toBeInTheDocument();
  });

  it('показує статус хвилини мовчання аудитору без права запуску команд', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      const path = String(input);
      const body = path.includes('/minute-of-silence/status/today')
        ? minuteStatus
        : path.includes('/minute-of-silence/history')
          ? {
              items: [],
              from: '2026-10-08',
              to: '2026-11-06',
              page: 1,
              pageSize: 25,
              totalCount: 0,
            }
          : { items: [], page: 1, pageSize: 25, totalCount: 0 };
      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <AuditPage
        session={{
          status: 'authenticated',
          roles: ['auditor'],
          capabilities: { canOperate: false, canViewAudit: true },
        }}
        onSessionExpired={vi.fn()}
        onSessionRetry={vi.fn()}
      />,
    );

    expect(await screen.findByRole('region', { name: 'Хвилина мовчання' })).toHaveTextContent(
      'Результат ще не зафіксовано.',
    );
    expect(await screen.findByRole('region', { name: 'Історія результатів' })).toHaveTextContent(
      'За цей період збережених результатів немає.',
    );
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/minute-of-silence/status/today',
      expect.objectContaining({ cache: 'no-store' }),
    );
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringMatching(/^\/api\/v1\/minute-of-silence\/history\?/),
      expect.objectContaining({ cache: 'no-store' }),
    );
  });

  it('не показує історію хвилини мовчання оператору без доступу до аудиту', () => {
    const fetchMock = vi.fn<typeof fetch>();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <AuditPage
        session={{
          status: 'authenticated',
          roles: ['notificator'],
          capabilities: { canOperate: true, canViewAudit: false },
        }}
        onSessionExpired={vi.fn()}
        onSessionRetry={vi.fn()}
      />,
    );

    expect(screen.queryByRole('region', { name: 'Історія результатів' })).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalledWith(
      expect.stringMatching(/^\/api\/v1\/minute-of-silence\/history\?/),
      expect.anything(),
    );
  });

  it('передає actor і локальний часовий діапазон як UTC та повертає на першу сторінку', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      const path = String(input);
      const body = path.includes('/minute-of-silence/status/today')
        ? minuteStatus
        : {
            items: [],
            page: Number(new URL(path, window.location.origin).searchParams.get('page')),
            pageSize: 25,
            totalCount: 50,
          };
      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <AuditPage session={auditSession} onSessionExpired={vi.fn()} onSessionRetry={vi.fn()} />,
    );

    expect(await screen.findByText('Записів: 50')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Наступна' }));
    await waitFor(() => {
      const auditRequests = fetchMock.mock.calls.filter(([input]) =>
        String(input).includes('/api/v1/audit-events'),
      );
      expect(
        new URL(String(auditRequests.at(-1)?.[0]), window.location.origin).searchParams.get('page'),
      ).toBe('2');
    });

    const actorId = 'c6b2f302-0675-4a4d-88eb-cf18c9022920';
    const eventType = 'auth.login.succeeded';
    const fromLocal = '2026-11-05T10:00';
    const toLocal = '2026-11-05T11:00';
    fireEvent.change(screen.getByRole('textbox', { name: 'Тип події' }), {
      target: { value: eventType },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'ID актора' }), {
      target: { value: actorId },
    });
    fireEvent.change(screen.getByLabelText('Від (час Києва)'), {
      target: { value: fromLocal },
    });
    fireEvent.change(screen.getByLabelText('До (час Києва)'), {
      target: { value: toLocal },
    });
    await user.click(screen.getByRole('button', { name: 'Застосувати фільтр' }));

    await waitFor(() => {
      const auditRequests = fetchMock.mock.calls.filter(([input]) =>
        String(input).includes('/api/v1/audit-events'),
      );
      const url = new URL(String(auditRequests.at(-1)?.[0]), window.location.origin);
      expect(url.searchParams.get('page')).toBe('1');
      expect(url.searchParams.get('eventType')).toBe(eventType);
      expect(url.searchParams.get('actorId')).toBe(actorId);
      expect(url.searchParams.get('fromUtc')).toBe('2026-11-05T08:00:00.000Z');
      expect(url.searchParams.get('toUtc')).toBe('2026-11-05T09:00:00.000Z');
    });
  });

  it('показує помилку й не надсилає запит із неповним часовим діапазоном', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      const body = String(input).includes('/minute-of-silence/status/today')
        ? minuteStatus
        : String(input).includes('/minute-of-silence/history')
          ? minuteHistory
          : { items: [], page: 1, pageSize: 25, totalCount: 0 };
      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <AuditPage session={auditSession} onSessionExpired={vi.fn()} onSessionRetry={vi.fn()} />,
    );

    await screen.findByRole('heading', { name: 'Журнал дій' });
    const auditRequestCount = () =>
      fetchMock.mock.calls.filter(([input]) => String(input).includes('/api/v1/audit-events'))
        .length;
    await waitFor(() => expect(auditRequestCount()).toBe(1));
    fireEvent.change(screen.getByLabelText('Від (час Києва)'), {
      target: { value: '2026-11-05T10:00' },
    });
    await user.click(screen.getByRole('button', { name: 'Застосувати фільтр' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Укажіть і початок, і кінець часового діапазону.',
    );
    expect(auditRequestCount()).toBe(1);

    fireEvent.change(screen.getByLabelText('До (час Києва)'), {
      target: { value: '2026-11-05T09:00' },
    });
    await user.click(screen.getByRole('button', { name: 'Застосувати фільтр' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Початок діапазону має бути раніше за завершення.',
    );
    expect(auditRequestCount()).toBe(1);

    fireEvent.change(screen.getByLabelText('До (час Києва)'), {
      target: { value: '2027-02-04T10:00' },
    });
    await user.click(screen.getByRole('button', { name: 'Застосувати фільтр' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Діапазон не може перевищувати 90 днів.',
    );
    expect(auditRequestCount()).toBe(1);
  });

  it.each([
    {
      value: '2026-03-29T03:30',
      message: 'У Києві цей час не існував через перехід на літній час.',
    },
    {
      value: '2026-10-25T03:30',
      message: 'У Києві цей час повторюється через перехід на зимовий час.',
    },
  ])('відхиляє DST-значення $value у часовому поясі Києва', async ({ value, message }) => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      const body = String(input).includes('/minute-of-silence/status/today')
        ? minuteStatus
        : String(input).includes('/minute-of-silence/history')
          ? minuteHistory
          : { items: [], page: 1, pageSize: 25, totalCount: 0 };
      return Promise.resolve(
        new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <AuditPage session={auditSession} onSessionExpired={vi.fn()} onSessionRetry={vi.fn()} />,
    );

    const auditRequestCount = () =>
      fetchMock.mock.calls.filter(([input]) => String(input).includes('/api/v1/audit-events'))
        .length;
    await waitFor(() => expect(auditRequestCount()).toBe(1));
    fireEvent.change(screen.getByLabelText('Від (час Києва)'), { target: { value } });
    fireEvent.change(screen.getByLabelText('До (час Києва)'), {
      target: { value: '2026-11-06T10:00' },
    });
    await user.click(screen.getByRole('button', { name: 'Застосувати фільтр' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(message);
    expect(auditRequestCount()).toBe(1);
  });
});
