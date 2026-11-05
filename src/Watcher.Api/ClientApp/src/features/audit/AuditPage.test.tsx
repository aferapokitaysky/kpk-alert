import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuditPage } from './AuditPage';

const minuteStatus = {
  runDate: '2026-11-06',
  timeZone: 'Europe/Kyiv',
  scheduledAtUtc: '2026-11-06T07:00:00Z',
  outcome: null,
  skipReason: null,
  commandId: null,
  processedAtUtc: null,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('сторінка аудиту', () => {
  it('показує статус хвилини мовчання аудитору без права запуску команд', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((input) => {
      const path = String(input);
      const body = path.includes('/minute-of-silence/status/today')
        ? minuteStatus
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
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/minute-of-silence/status/today',
      expect.objectContaining({ cache: 'no-store' }),
    );
  });
});
