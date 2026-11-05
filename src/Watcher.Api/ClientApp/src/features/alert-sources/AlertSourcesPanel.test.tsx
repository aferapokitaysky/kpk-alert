import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AlertSourcesPanel } from './AlertSourcesPanel';

const statusResponse = {
  checkedAtUtc: '2026-11-05T10:00:00Z',
  targets: [
    {
      scope: 'hromada',
      scopeId: '1088',
      scopeName: 'Кременчуцька громада',
      level: 'active',
      alertLevel: 'red',
      freshness: 'fresh',
      checkedAtUtc: '2026-11-05T10:00:00Z',
      dataUpdatedAtUtc: '2026-11-05T09:58:00Z',
      authoritativeObservation: {
        source: 'alerts_in_ua',
        sourceName: 'alerts.in.ua',
        scope: 'hromada',
        scopeId: '1088',
        scopeName: 'Кременчуцька громада',
        level: 'active',
        alertLevel: 'red',
        freshness: 'fresh',
        checkedAtUtc: '2026-11-05T10:00:00Z',
        dataUpdatedAtUtc: '2026-11-05T09:58:00Z',
        errorCode: null,
        attributionUrl: 'https://alerts.in.ua/',
        lastKnownLevel: null,
        lastKnownAlertLevel: null,
      },
      observations: [
        {
          source: 'alerts_in_ua',
          sourceName: 'alerts.in.ua',
          scope: 'hromada',
          scopeId: '1088',
          scopeName: 'Кременчуцька громада',
          level: 'active',
          alertLevel: 'red',
          freshness: 'fresh',
          checkedAtUtc: '2026-11-05T10:00:00Z',
          dataUpdatedAtUtc: '2026-11-05T09:58:00Z',
          errorCode: null,
          attributionUrl: 'https://alerts.in.ua/',
          lastKnownLevel: null,
          lastKnownAlertLevel: null,
        },
      ],
    },
  ],
};

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('панель джерел тривог', () => {
  it('показує статус громади та посилання на джерело', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(statusResponse), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<AlertSourcesPanel />);

    expect(screen.getByRole('status')).toHaveTextContent('Завантажуємо статуси джерел');
    expect(await screen.findAllByText('Тривога активна · червоний рівень')).toHaveLength(2);
    expect(screen.getByRole('link', { name: 'Дані: alerts.in.ua' })).toHaveAttribute(
      'href',
      'https://alerts.in.ua/',
    );
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/alert-sources/status',
      expect.objectContaining({ cache: 'no-store' }),
    );
  });

  it('показує помилку оновлення, а не стан без тривоги', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}', { status: 429 })));

    render(<AlertSourcesPanel />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Сервер тимчасово обмежив оновлення статусу',
    );
    expect(screen.queryByText('Тривоги немає')).not.toBeInTheDocument();
  });

  it('не відкриває посилання з невідомою схемою URL у відповіді', async () => {
    const unsafeResponse = structuredClone(statusResponse);
    const unsafeTarget = unsafeResponse.targets[0];
    const unsafeObservation = unsafeTarget?.observations[0];
    const authoritativeObservation = unsafeTarget?.authoritativeObservation;
    if (!unsafeTarget || !unsafeObservation || !authoritativeObservation) {
      throw new Error('Missing fixture observation');
    }
    unsafeObservation.attributionUrl = 'javascript:alert(1)';
    authoritativeObservation.attributionUrl = 'javascript:alert(1)';
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify(unsafeResponse), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );

    render(<AlertSourcesPanel />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Не вдалося отримати статус тривог');
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('позначає застарілі дані як невідомий поточний стан і відокремлює останній відомий', async () => {
    const primaryTarget = statusResponse.targets[0];
    const primaryObservation = primaryTarget?.observations[0];
    if (!primaryTarget || !primaryObservation) throw new Error('Missing fixture observation');

    const staleObservation = {
      ...primaryObservation,
      level: 'unknown',
      alertLevel: null,
      freshness: 'stale',
      errorCode: 'source_unavailable',
      lastKnownLevel: 'active',
      lastKnownAlertLevel: 'red',
    };
    const staleTarget = {
      ...primaryTarget,
      level: 'unknown',
      alertLevel: null,
      freshness: 'stale',
      authoritativeObservation: staleObservation,
      observations: [staleObservation],
    };
    const staleResponse = { ...statusResponse, targets: [staleTarget] };
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify(staleResponse), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );

    render(<AlertSourcesPanel />);

    await waitFor(() => expect(screen.getByText('Поточний статус невідомий')).toBeInTheDocument());
    expect(screen.getByText(/Останній відомий статус: Тривога активна/)).toBeInTheDocument();
    expect(screen.queryByText('Тривоги немає')).not.toBeInTheDocument();
  });
});
