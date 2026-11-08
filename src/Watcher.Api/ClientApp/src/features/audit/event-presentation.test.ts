import { describe, expect, it } from 'vitest';
import type { AuditEvent } from '../../api';
import { presentAuditEvent } from './event-presentation';

describe('представлення помилок джерела в аудиті', () => {
  it.each([
    ['action_index_rollback', 'провайдер повернув менший індекс подій'],
    ['action_index_not_advanced', 'після callback індекс подій не змінився'],
    ['snapshot_changed_during_read', 'стан змінився під час читання даних'],
    ['region_not_found', 'у відповіді провайдера не знайдено область або громаду'],
  ])('перекладає код %s', (errorCode, expectedReason) => {
    const event: AuditEvent = {
      id: 'event-id',
      eventType: 'integration.alert_source.state_changed',
      actorType: 'integration',
      actorId: null,
      occurredAtUtc: '2026-11-08T12:00:00Z',
      payload: { errorCode: 'source_unavailable', previousErrorCode: errorCode },
    };

    const presentation = presentAuditEvent(event);

    expect(presentation.details).toContain(`Попередня причина: ${expectedReason}`);
  });
});
