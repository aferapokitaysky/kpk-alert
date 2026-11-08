import type { AuditEvent } from '../../api';

interface EventPresentation {
  title: string;
  details: string[];
  known: boolean;
}

function payloadRecord(payload: unknown): Record<string, unknown> {
  if (typeof payload === 'string') {
    try {
      const parsed: unknown = JSON.parse(payload);
      if (typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed))
        return parsed as Record<string, unknown>;
    } catch {
      return {};
    }
  }
  return typeof payload === 'object' && payload !== null && !Array.isArray(payload)
    ? (payload as Record<string, unknown>)
    : {};
}

function value(record: Record<string, unknown>, key: string): unknown {
  const foundKey = Object.keys(record).find(
    (candidate) => candidate.toLowerCase() === key.toLowerCase(),
  );
  return foundKey ? record[foundKey] : undefined;
}

function safeText(record: Record<string, unknown>, key: string): string | undefined {
  const candidate = value(record, key);
  return typeof candidate === 'string' &&
    candidate.length <= 120 &&
    /^[\p{L}\p{N}_.:-]+$/u.test(candidate)
    ? candidate
    : undefined;
}

function safeId(record: Record<string, unknown>, key: string): string | undefined {
  const candidate = safeText(record, key);
  return candidate && /^[0-9a-f-]{36}$/i.test(candidate) ? candidate : undefined;
}

function safeTimestamp(record: Record<string, unknown>, key: string): string | undefined {
  const candidate = value(record, key);
  if (typeof candidate !== 'string' || candidate.length > 40) return undefined;
  const timestamp = new Date(candidate);
  if (Number.isNaN(timestamp.getTime())) return undefined;
  return new Intl.DateTimeFormat('uk-UA', {
    dateStyle: 'medium',
    timeStyle: 'short',
    timeZone: 'Europe/Kyiv',
  }).format(timestamp);
}

function deliveryStateLabel(state: string): string | undefined {
  const states: Record<string, string> = {
    Pending: 'очікує надсилання',
    InFlight: 'надсилається',
    Accepted: 'прийнято вузлом',
    Playing: 'запущено програмний шлях відтворення',
    Completed: 'виконано',
    Stopped: 'зупинено',
    Rejected: 'відхилено вузлом',
    Failed: 'не виконано',
    Expired: 'строк дії минув',
  };
  return states[state];
}

function addDeliveryTransitionDetails(details: string[], payload: Record<string, unknown>): void {
  const before = safeText(payload, 'deliveryStateBefore') ?? safeText(payload, 'stateBefore');
  const after = safeText(payload, 'deliveryStateAfter') ?? safeText(payload, 'stateAfter');
  if (!before || !after) return;

  const beforeLabel = deliveryStateLabel(before);
  const afterLabel = deliveryStateLabel(after);
  if (beforeLabel && afterLabel) details.push(`Стан доставки: ${beforeLabel} → ${afterLabel}`);
}

function addMinuteOfSilenceScheduleDetails(
  details: string[],
  payload: Record<string, unknown>,
): void {
  const date = safeText(payload, 'scheduledDate') ?? safeText(payload, 'runDate');
  if (date && /^\d{4}-\d{2}-\d{2}$/.test(date)) details.push(`Дата розкладу: ${date}`);

  const localTime = safeText(payload, 'scheduledLocalTime');
  if (localTime && /^\d{2}:\d{2}$/.test(localTime))
    details.push(`Місцевий час запуску: ${localTime}`);

  const timeZone = value(payload, 'scheduleTimeZoneId') ?? value(payload, 'timeZone');
  if (timeZone === 'Europe/Kyiv') details.push('Часовий пояс: Київ');

  const scheduledAt = safeTimestamp(payload, 'scheduledAtUtc');
  if (scheduledAt) details.push(`Заплановано за київським часом: ${scheduledAt}`);
}

function addObservationDetails(details: string[], payload: Record<string, unknown>): void {
  const alertLevel = safeText(payload, 'alertLevel')?.toLowerCase();
  const alertLevels: Record<string, string> = {
    red: 'червоний',
    yellow: 'жовтий',
    unknown: 'невідомий',
  };
  if (alertLevel)
    details.push(
      `Рівень спостереження джерела: ${alertLevels[alertLevel] ?? 'інше значення'} (не гучність)`,
    );

  const source = safeText(payload, 'source')?.toLowerCase();
  const sourceNames: Record<string, string> = {
    ukrainealarm: 'UkraineAlarm',
    neptun: 'Neptun',
    'alerts.in.ua': 'Alerts in UA',
  };
  if (source && sourceNames[source]) details.push(`Джерело спостереження: ${sourceNames[source]}`);

  const scope = safeText(payload, 'scope')?.toLowerCase();
  const scopes: Record<string, string> = {
    oblast: 'область',
    raion: 'район',
    hromada: 'громада',
    city: 'місто',
  };
  if (scope && scopes[scope]) details.push(`Область спостереження: ${scopes[scope]}`);
  if (safeText(payload, 'scopeId'))
    details.push(`Код області спостереження: ${safeText(payload, 'scopeId')}`);

  const checkedAt = safeTimestamp(payload, 'checkedAtUtc');
  if (checkedAt) details.push(`Час спостереження: ${checkedAt}`);
  const updatedAt = safeTimestamp(payload, 'dataUpdatedAtUtc');
  if (updatedAt) details.push(`Дані джерела оновлено: ${updatedAt}`);
}

function observationSummary(observation: Record<string, unknown>): string {
  const parts: string[] = [];
  const source = safeText(observation, 'source')?.toLowerCase();
  const sources: Record<string, string> = {
    ukrainealarm: 'UkraineAlarm',
    neptun: 'Neptun',
    'alerts.in.ua': 'Alerts in UA',
  };
  parts.push(source && sources[source] ? sources[source] : 'інше джерело');

  const scope = safeText(observation, 'scope')?.toLowerCase();
  const scopes: Record<string, string> = {
    oblast: 'область',
    raion: 'район',
    hromada: 'громада',
    city: 'місто',
  };
  if (scope && scopes[scope]) parts.push(scopes[scope]);
  if (safeText(observation, 'scopeId')) parts.push(`код ${safeText(observation, 'scopeId')}`);

  const level = safeText(observation, 'level')?.toLowerCase();
  const levels: Record<string, string> = {
    active: 'тривога активна',
    inactive: 'стан неактивний',
    partial: 'неповні дані',
    conflict: 'суперечливі дані',
    unknown: 'невідомий стан',
  };
  if (level && levels[level]) parts.push(levels[level]);

  const alertLevel = safeText(observation, 'alertLevel')?.toLowerCase();
  const alertLevels: Record<string, string> = {
    red: 'червоний',
    yellow: 'жовтий',
    unknown: 'невідомий',
  };
  if (alertLevel)
    parts.push(`рівень API: ${alertLevels[alertLevel] ?? 'інше значення'} (не гучність)`);

  const freshness = safeText(observation, 'freshness')?.toLowerCase();
  const freshnessLabels: Record<string, string> = {
    fresh: 'свіжі дані',
    stale: 'застарілі дані',
    unknown: 'актуальність невідома',
  };
  if (freshness && freshnessLabels[freshness]) parts.push(freshnessLabels[freshness]);
  return parts.join(' · ');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function detailsFor(eventType: string, payload: Record<string, unknown>): string[] {
  const details: string[] = [];
  const commandId =
    safeId(payload, 'commandId') ?? safeId(payload, 'id') ?? safeId(payload, 'targetCommandId');
  if (commandId) details.push(`Команда: ${commandId}`);
  if (safeId(payload, 'deliveryId')) details.push(`Доставка: ${safeId(payload, 'deliveryId')}`);
  addDeliveryTransitionDetails(details, payload);

  switch (eventType) {
    case 'alert_command.created': {
      if (safeText(payload, 'action') === 'start') details.push('Дія: запуск сигналу');
      if (safeText(payload, 'action') === 'stop') details.push('Дія: зупинка сигналу');
      if (safeText(payload, 'presetCode'))
        details.push(`Сценарій: ${safeText(payload, 'presetCode')}`);
      if (safeText(payload, 'presetCode') === 'minute_of_silence')
        addMinuteOfSilenceScheduleDetails(details, payload);
      addObservationDetails(details, payload);
      break;
    }
    case 'alert_command.stop_requested':
      if (safeId(payload, 'targetCommandId'))
        details.push(`Зупиняє команду: ${safeId(payload, 'targetCommandId')}`);
      break;
    case 'alert_command.delivery_stopped':
      if (safeId(payload, 'stopCommandId'))
        details.push(`Команда зупинки: ${safeId(payload, 'stopCommandId')}`);
      if (safeId(payload, 'stopDeliveryId'))
        details.push(`Доставка команди зупинки: ${safeId(payload, 'stopDeliveryId')}`);
      break;
    case 'alert_command.receipt_received': {
      const state = safeText(payload, 'state');
      const stateLabels: Record<string, string> = {
        Accepted: 'прийнято вузлом',
        Playing: 'запущено програмний шлях відтворення',
        Stopped: 'зупинено',
        Rejected: 'відхилено',
        Expired: 'термін дії минув на вузлі',
      };
      if (state && stateLabels[state]) details.push(`Підтвердження вузла: ${stateLabels[state]}`);
      if (safeText(payload, 'diagnosticCode'))
        details.push(`Код діагностики: ${safeText(payload, 'diagnosticCode')}`);
      break;
    }
    case 'alert_command.device_playback_state_reported': {
      const state = safeText(payload, 'state');
      const states: Record<string, string> = {
        Playing: 'запущено програмний шлях відтворення',
        Completed: 'програмний шлях відтворення завершився',
        Stopped: 'програмний шлях відтворення зупинено',
      };
      if (state && states[state])
        details.push(`Вузол повідомив: ${states[state]}; звук із динаміка не вимірювався.`);
      break;
    }
    case 'alert_command.delivery_retry_scheduled': {
      const count = value(payload, 'attemptCount');
      if (typeof count === 'number' && Number.isInteger(count) && count >= 0 && count <= 10000)
        details.push(`Спроба доставки: ${count}`);
      const reason = safeText(payload, 'reasonCode') ?? safeText(payload, 'reason');
      const reasons: Record<string, string> = {
        connection_unavailable: 'зв’язок тимчасово недоступний',
        device_unavailable: 'вузол недоступний',
        presence_session_changed: 'сесія вузла змінилася',
        send_not_started: 'надсилання не розпочалося',
        socket_write_result_unknown: 'результат запису не підтверджений',
        socket_write_timed_out_result_unknown: 'час очікування минув без підтвердженого результату',
      };
      if (reason && reasons[reason]) details.push(`Причина: ${reasons[reason]}`);
      break;
    }
    case 'alert_command.delivery_attempt_started': {
      const count = value(payload, 'attemptCount');
      if (typeof count === 'number' && Number.isInteger(count) && count > 0 && count <= 10000)
        details.push(`Спроба надсилання: ${count}`);
      const action = safeText(payload, 'action');
      if (action === 'start') details.push('Дія: запуск сигналу');
      if (action === 'stop') details.push('Дія: зупинка сигналу');
      if (safeText(payload, 'presetCode'))
        details.push(`Сценарій: ${safeText(payload, 'presetCode')}`);
      break;
    }
    case 'alert_command.delivery_write_result': {
      const outcome = safeText(payload, 'outcome');
      const outcomes: Record<string, string> = {
        completed: 'Запис команди у WebSocket завершився; отримання вузлом не підтверджене.',
        not_started: 'Запис команди не розпочався.',
        failed: 'Спроба запису не завершилася успішно; перевірте квитанцію вузла.',
        indeterminate: 'Результат передавання невідомий; перевірте квитанцію вузла.',
      };
      if (outcome && outcomes[outcome]) details.push(outcomes[outcome]);
      const reason = safeText(payload, 'reasonCode');
      const reasons: Record<string, string> = {
        socket_write_completed: 'запис WebSocket завершено',
        send_timeout: 'вичерпано час очікування запису',
        socket_write_result_unknown: 'виклик запису не повернув підтвердженого результату',
        socket_write_timed_out_result_unknown:
          'час очікування запису минув без підтвердженого результату',
        device_unavailable: 'вузол недоступний',
        presence_session_changed: 'сесія вузла змінилася',
        session_unavailable: 'сесія вузла недоступна',
      };
      if (reason && reasons[reason]) details.push(`Причина: ${reasons[reason]}`);
      break;
    }
    case 'alert_command.delivery_failed': {
      const reason = safeText(payload, 'reason');
      const reasons: Record<string, string> = {
        device_unavailable: 'вузол недоступний',
        command_target_missing: 'вузол не входить до команди',
        invalid_command: 'команда не пройшла перевірку',
      };
      if (reason && reasons[reason]) details.push(`Причина: ${reasons[reason]}`);
      break;
    }
    case 'alert_command.delivery_expired': {
      if (safeText(payload, 'reason') === 'command_expired')
        details.push('Причина: строк дії команди минув');
      break;
    }
    case 'auth.login.succeeded':
    case 'auth.login.rejected':
    case 'auth.logout': {
      const method = safeText(payload, 'method');
      if (method && ['google', 'session'].includes(method))
        details.push(`Спосіб: ${method === 'google' ? 'Google' : 'сесія'}`);
      const reason = safeText(payload, 'reason');
      const reasons: Record<string, string> = {
        provider_failure: 'помилка постачальника входу',
        email_unverified: 'обліковий запис не підтверджено',
        account_not_allowed: 'обліковий запис не має доступу',
      };
      if (reason && reasons[reason]) details.push(`Причина: ${reasons[reason]}`);
      break;
    }
    case 'user.role_granted':
    case 'user.role_revoked':
      if (safeId(payload, 'targetUserId'))
        details.push(`Обліковий запис: ${safeId(payload, 'targetUserId')}`);
      if (safeText(payload, 'roleCode')) details.push(`Роль: ${safeText(payload, 'roleCode')}`);
      break;
    case 'user.status_changed':
      if (typeof value(payload, 'isActive') === 'boolean')
        details.push(value(payload, 'isActive') ? 'Статус: активний' : 'Статус: вимкнений');
      break;
    case 'automatic_alert.stop_not_required': {
      if (safeId(payload, 'targetCommandId'))
        details.push(`Команда: ${safeId(payload, 'targetCommandId')}`);
      if (safeText(payload, 'reasonCode') === 'no_active_routable_deliveries')
        details.push('Зупинку не потрібно надсилати: немає активних доступних вузлів.');
      break;
    }
    case 'automatic_alert.held': {
      const decisionCode = safeText(payload, 'decisionCode');
      const decisions: Record<string, string> = {
        invalid_open_episode: 'поточний епізод має некоректну область',
        no_fresh_observation: 'немає свіжого спостереження',
        observation_not_actionable: 'спостереження недостатньо однозначне для дії',
        awaiting_geographic_clear: 'очікується підтвердження завершення для потрібної області',
        preset_unavailable: 'сценарій звуку недоступний',
        preset_manifest_invalid: 'опис сценарію некоректний',
        no_active_locations: 'немає активних локацій',
        awaiting_start_delivery: 'очікується доставка команди запуску',
      };
      if (decisionCode && decisions[decisionCode])
        details.push(`Рішення: ${decisions[decisionCode]}`);
      const observations = value(payload, 'observations');
      if (Array.isArray(observations)) {
        observations.slice(0, 24).forEach((observation, index) => {
          details.push(
            `Спостереження ${index + 1}: ${isRecord(observation) ? observationSummary(observation) : 'невідомі дані'}`,
          );
          if (isRecord(observation)) {
            const checkedAt = safeTimestamp(observation, 'checkedAtUtc');
            if (checkedAt) details.push(`Час спостереження ${index + 1}: ${checkedAt}`);
            const updatedAt = safeTimestamp(observation, 'dataUpdatedAtUtc');
            if (updatedAt) details.push(`Час оновлення джерела ${index + 1}: ${updatedAt}`);
          }
        });
        if (value(payload, 'observationsTruncated') === true)
          details.push('Частину спостережень не показано.');
      } else {
        addObservationDetails(details, payload);
      }
      break;
    }
    case 'minute_of_silence.skipped': {
      addMinuteOfSilenceScheduleDetails(details, payload);
      const reason = safeText(payload, 'reason') ?? safeText(payload, 'skipReason');
      const reasons: Record<string, string> = {
        preset_unavailable: 'сценарій звуку недоступний',
        preset_manifest_invalid: 'опис сценарію некоректний',
        no_active_locations: 'немає активних локацій',
        outside_execution_window: 'поза вікном виконання',
        missed_window: 'час запуску минув',
      };
      if (reason && reasons[reason]) details.push(`Причина: ${reasons[reason]}`);
      break;
    }
    case 'sound_preset.created':
    case 'sound_preset.updated':
    case 'sound_preset.publication_changed': {
      if (safeId(payload, 'presetId')) details.push(`Сценарій: ${safeId(payload, 'presetId')}`);
      if (safeText(payload, 'code')) details.push(`Код сценарію: ${safeText(payload, 'code')}`);
      if (typeof value(payload, 'isPublished') === 'boolean')
        details.push(
          value(payload, 'isPublished') ? 'Публікація: активна' : 'Публікація: вимкнена',
        );
      break;
    }
    case 'audio_asset.uploaded':
    case 'audio_asset.deleted': {
      if (safeId(payload, 'assetId')) details.push(`Аудіофайл: ${safeId(payload, 'assetId')}`);
      if (safeText(payload, 'presetCode'))
        details.push(`Код сценарію: ${safeText(payload, 'presetCode')}`);
      const size = value(payload, 'sizeBytes');
      if (typeof size === 'number' && Number.isSafeInteger(size) && size >= 0)
        details.push(`Розмір: ${size} байт`);
      break;
    }
    case 'device.registered':
    case 'device.revoked':
    case 'device.certificate_rotation_staged':
    case 'device.certificate_rotation_cancelled':
    case 'device.certificate_rotated':
      if (safeId(payload, 'deviceId')) details.push(`Вузол: ${safeId(payload, 'deviceId')}`);
      break;
    case 'device.session_connected':
    case 'device.session_superseded':
    case 'device.session_disconnected':
      if (safeId(payload, 'id')) details.push(`Сесія: ${safeId(payload, 'id')}`);
      if (safeId(payload, 'deviceId')) details.push(`Вузол: ${safeId(payload, 'deviceId')}`);
      if (safeId(payload, 'locationId')) details.push(`Локація: ${safeId(payload, 'locationId')}`);
      break;
    case 'integration.ukrainealarm_webhook.requested':
    case 'integration.ukrainealarm_webhook.resolved':
    case 'integration.ukrainealarm_webhook.state_reconciled':
    case 'integration.ukrainealarm_webhook.pending':
    case 'integration.ukrainealarm_webhook.running':
    case 'integration.ukrainealarm_webhook.unknown':
    case 'integration.ukrainealarm_webhook.succeeded':
    case 'integration.ukrainealarm_webhook.rejected': {
      if (safeId(payload, 'operationId'))
        details.push(`Операція: ${safeId(payload, 'operationId')}`);
      const action = safeText(payload, 'action');
      if (action && ['subscribe', 'unsubscribe'].includes(action))
        details.push(`Дія: ${action === 'subscribe' ? 'підписка' : 'скасування підписки'}`);
      const registered = value(payload, 'isRegistered');
      if (typeof registered === 'boolean')
        details.push(
          registered ? 'Стан підписки: зареєстрована' : 'Стан підписки: не зареєстрована',
        );
      const outcome = safeText(payload, 'outcome') ?? safeText(payload, 'outcomeCode');
      const outcomes: Record<string, string> = {
        requested: 'операцію прийнято сервером',
        provider_request_started: 'запит надіслано провайдеру',
        provider_accepted: 'провайдер підтвердив запит',
        provider_rejected: 'провайдер відхилив запит',
        upstream_indeterminate: 'провайдер не підтвердив результат',
        transport_indeterminate: 'не вдалося підтвердити результат мережевого запиту',
        configuration_invalid: 'конфігурація інтеграції некоректна',
        manually_confirmed_registered: 'адміністратор підтвердив наявність підписки',
        manually_confirmed_absent: 'адміністратор підтвердив відсутність підписки',
        operator_confirmed: 'адміністратор звірив стан підписки',
      };
      if (outcome && outcomes[outcome]) details.push(`Результат: ${outcomes[outcome]}`);
      break;
    }
    case 'integration.alert_source.state_changed': {
      const source = safeText(payload, 'source')?.toLowerCase();
      const sources: Record<string, string> = {
        ukrainealarm: 'UkraineAlarm',
        'alerts.in.ua': 'Alerts in UA',
        neptun: 'NEPTUN',
      };
      if (source && sources[source]) details.push(`Джерело: ${sources[source]}`);

      const scope = safeText(payload, 'scope')?.toLowerCase();
      const scopes: Record<string, string> = {
        oblast: 'область',
        raion: 'район',
        hromada: 'громада',
      };
      if (scope && scopes[scope]) details.push(`Область: ${scopes[scope]}`);
      if (safeText(payload, 'scopeId'))
        details.push(`Код області: ${safeText(payload, 'scopeId')}`);

      const levelLabels: Record<string, string> = {
        active: 'тривога активна',
        inactive: 'стан неактивний',
        partial: 'неповні дані',
        unknown: 'невідомий стан',
        conflict: 'суперечливі дані',
      };
      const level = safeText(payload, 'level')?.toLowerCase();
      if (level && levelLabels[level]) details.push(`Стан джерела: ${levelLabels[level]}`);

      const alertLevels: Record<string, string> = {
        red: 'червоний',
        yellow: 'жовтий',
        unknown: 'невідомий',
      };
      const alertLevel = safeText(payload, 'alertLevel')?.toLowerCase();
      if (alertLevel && alertLevels[alertLevel])
        details.push(`Рівень API: ${alertLevels[alertLevel]} (не гучність)`);

      const freshnessLabels: Record<string, string> = {
        fresh: 'свіжі дані',
        stale: 'застарілі дані',
        unknown: 'актуальність невідома',
      };
      const freshness = safeText(payload, 'freshness')?.toLowerCase();
      if (freshness && freshnessLabels[freshness])
        details.push(`Актуальність: ${freshnessLabels[freshness]}`);

      const previousFreshness = safeText(payload, 'previousFreshness')?.toLowerCase();
      if (previousFreshness && freshnessLabels[previousFreshness])
        details.push(`Попередня актуальність: ${freshnessLabels[previousFreshness]}`);

      const lastKnownLevel = safeText(payload, 'lastKnownLevel')?.toLowerCase();
      if (lastKnownLevel && levelLabels[lastKnownLevel])
        details.push(`Останній відомий стан: ${levelLabels[lastKnownLevel]}`);

      const lastKnownAlertLevel = safeText(payload, 'lastKnownAlertLevel')?.toLowerCase();
      if (lastKnownAlertLevel && alertLevels[lastKnownAlertLevel])
        details.push(
          `Останній відомий рівень API: ${alertLevels[lastKnownAlertLevel]} (не гучність)`,
        );

      const changeType = safeText(payload, 'changeType');
      if (changeType === 'initial') details.push('Зафіксовано початковий стан джерела');
      if (changeType === 'changed') details.push('Нормалізований стан джерела змінився');

      const previousLevel = safeText(payload, 'previousLevel')?.toLowerCase();
      if (previousLevel && levelLabels[previousLevel])
        details.push(`Попередній стан: ${levelLabels[previousLevel]}`);
      const previousAlertLevel = safeText(payload, 'previousAlertLevel')?.toLowerCase();
      if (previousAlertLevel && alertLevels[previousAlertLevel])
        details.push(`Попередній рівень API: ${alertLevels[previousAlertLevel]} (не гучність)`);

      const checkedAt = safeTimestamp(payload, 'checkedAtUtc');
      if (checkedAt) details.push(`Перевірено джерело: ${checkedAt}`);
      const updatedAt = safeTimestamp(payload, 'dataUpdatedAtUtc');
      if (updatedAt) details.push(`Дані джерела оновлено: ${updatedAt}`);

      const errors: Record<string, string> = {
        not_configured: 'джерело не налаштовано',
        source_unavailable: 'джерело недоступне',
        invalid_response: 'джерело повернуло некоректну відповідь',
        rate_limited: 'джерело обмежило частоту запитів',
        request_failed: 'запит до джерела не виконано',
        response_too_large: 'відповідь джерела завелика',
        heartbeat_timeout: 'вичерпано час очікування heartbeat',
        retry_backoff: 'очікується повторний запит',
        action_index_rollback: 'провайдер повернув менший індекс подій',
        action_index_not_advanced: 'після callback індекс подій не змінився',
        snapshot_changed_during_read: 'стан змінився під час читання даних',
        region_not_found: 'у відповіді провайдера не знайдено область або громаду',
      };
      const errorCode = safeText(payload, 'errorCode');
      if (errorCode && errors[errorCode]) details.push(`Причина: ${errors[errorCode]}`);
      const previousErrorCode = safeText(payload, 'previousErrorCode');
      if (previousErrorCode && errors[previousErrorCode])
        details.push(`Попередня причина: ${errors[previousErrorCode]}`);
      break;
    }
    default:
      break;
  }
  return details;
}

const titles: Record<string, string> = {
  'alert_command.created': 'Створено команду',
  'alert_command.stop_requested': 'Запитано зупинку команди',
  'alert_command.receipt_received': 'Отримано підтвердження від вузла',
  'alert_command.delivery_stopped': 'Вузол повідомив про зупинку',
  'alert_command.delivery_retry_scheduled': 'Заплановано повтор доставки',
  'alert_command.delivery_expired': 'Термін доставки минув',
  'alert_command.delivery_failed': 'Доставку не виконано',
  'alert_command.delivery_attempt_started': 'Розпочато спробу надсилання команди',
  'alert_command.delivery_write_result': 'Результат запису команди у WebSocket',
  'alert_command.device_playback_state_reported': 'Вузол повідомив стан відтворення',
  'auth.login.succeeded': 'Успішний вхід',
  'auth.login.rejected': 'Вхід відхилено',
  'auth.logout': 'Завершено сесію',
  'user.role_granted': 'Надано роль',
  'user.role_revoked': 'Скасовано роль',
  'user.status_changed': 'Змінено статус облікового запису',
  'device.registered': 'Зареєстровано вузол',
  'device.revoked': 'Відкликано вузол',
  'device.certificate_rotation_staged': 'Підготовлено заміну сертифіката вузла',
  'device.certificate_rotation_cancelled': 'Скасовано заміну сертифіката вузла',
  'device.certificate_rotated': 'Оновлено сертифікат вузла',
  'device.session_connected': 'Підключено сесію вузла',
  'device.session_superseded': 'Сесію вузла замінено',
  'device.session_disconnected': 'Сесію вузла завершено',
  'sound_preset.created': 'Створено сценарій звуку',
  'sound_preset.updated': 'Оновлено сценарій звуку',
  'sound_preset.publication_changed': 'Змінено публікацію сценарію',
  'audio_asset.uploaded': 'Додано аудіофайл',
  'audio_asset.deleted': 'Видалено аудіофайл',
  'automatic_alert.stop_not_required': 'Зупинка автоматичного сигналу не потрібна',
  'automatic_alert.held': 'Автоматичну дію відкладено',
  'minute_of_silence.skipped': 'Пропущено запуск хвилини мовчання',
  'integration.ukrainealarm_webhook.requested': 'Надіслано запит підписки на вебхук',
  'integration.ukrainealarm_webhook.resolved': 'Отримано результат запиту вебхука',
  'integration.ukrainealarm_webhook.state_reconciled': 'Оновлено стан підписки на вебхук',
  'integration.ukrainealarm_webhook.pending': 'Запит вебхука очікує обробки',
  'integration.ukrainealarm_webhook.running': 'Запит вебхука обробляється',
  'integration.ukrainealarm_webhook.unknown': 'Стан запиту вебхука невідомий',
  'integration.ukrainealarm_webhook.succeeded': 'Запит вебхука виконано',
  'integration.ukrainealarm_webhook.rejected': 'Запит вебхука відхилено',
  'integration.alert_source.state_changed': 'Змінено стан джерела тривоги',
};

export function presentAuditEvent(event: AuditEvent): EventPresentation {
  const title = titles[event.eventType];
  return {
    title: title ?? 'Подія системи',
    details: title ? detailsFor(event.eventType, payloadRecord(event.payload)) : [],
    known: Boolean(title),
  };
}
