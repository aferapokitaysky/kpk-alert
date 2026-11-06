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
