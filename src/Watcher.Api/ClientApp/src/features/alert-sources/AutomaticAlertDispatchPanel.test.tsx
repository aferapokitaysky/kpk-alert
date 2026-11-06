import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AutomaticAlertDispatchPanel } from './AutomaticAlertDispatchPanel';

function response(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function status(overrides: Record<string, unknown> = {}) {
  return {
    enabled: false,
    presetCode: 'air_raid',
    state: 'idle',
    scope: null,
    scopeId: null,
    startCommandId: null,
    startedAtUtc: null,
    lastDecisionCode: 'no_open_alert',
    updatedAtUtc: '2026-11-06T08:00:00Z',
    ...overrides,
  };
}

const fetchMock = vi.fn<typeof fetch>();

afterEach(() => {
  cleanup();
  fetchMock.mockReset();
  vi.unstubAllGlobals();
});

describe('стан автоматичного оповіщення', () => {
  it('показує вимкнений стан без обіцянки автоматичного запуску', async () => {
    fetchMock.mockResolvedValue(response(status()));
    vi.stubGlobal('fetch', fetchMock);

    render(<AutomaticAlertDispatchPanel />);

    expect(await screen.findByText('Вимкнена конфігурацією сервера')).toBeInTheDocument();
    expect(screen.getByText('Активної тривоги немає.')).toBeInTheDocument();
  });

  it('пояснює активний епізод і показує ідентифікатор команди', async () => {
    fetchMock.mockResolvedValue(
      response(
        status({
          enabled: true,
          state: 'alerting',
          scope: 'hromada',
          scopeId: '1088',
          startCommandId: 'command-1',
          startedAtUtc: '2026-11-06T08:00:00Z',
          lastDecisionCode: 'primary_alert_active',
        }),
      ),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<AutomaticAlertDispatchPanel />);

    expect(await screen.findByText('Тривогу підтверджено')).toBeInTheDocument();
    expect(screen.getByText('Запуск підтверджено UkraineAlarm.')).toBeInTheDocument();
    expect(screen.getByText('command-1')).toBeInTheDocument();
    expect(screen.getByText(/не є підтвердженням відтворення звуку/i)).toBeInTheDocument();
  });

  it('дає повторити завантаження після помилки', async () => {
    const user = userEvent.setup();
    fetchMock.mockRejectedValueOnce(new Error('network')).mockResolvedValue(response(status()));
    vi.stubGlobal('fetch', fetchMock);

    render(<AutomaticAlertDispatchPanel />);
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Не вдалося завантажити стан автоматичного оповіщення.',
    );
    await user.click(screen.getByRole('button', { name: 'Повторити' }));
    expect(await screen.findByText('Вимкнена конфігурацією сервера')).toBeInTheDocument();
  });
});
