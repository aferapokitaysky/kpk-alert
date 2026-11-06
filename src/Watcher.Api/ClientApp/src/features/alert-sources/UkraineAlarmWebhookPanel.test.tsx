import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { UkraineAlarmWebhookPanel } from './UkraineAlarmWebhookPanel';

const path = '/api/v1/admin/integrations/ukrainealarm/webhook-operations';

function state(
  registration: 'registered' | 'not_registered' | 'unknown',
  activeOperation: Record<string, unknown> | null = null,
) {
  return {
    integrationCode: 'ukrainealarm_webhook',
    state: registration,
    updatedAtUtc: '2026-11-06T10:00:00Z',
    activeOperation,
  };
}

function operation(operationState: string, action = 'register') {
  return {
    id: 'operation-1',
    action,
    state: operationState,
    callbackUrl: 'https://alerts.example.test/api/v1/alert-sources/ukrainealarm/webhook',
    outcomeCode: null,
    createdAtUtc: '2026-11-06T10:00:00Z',
    updatedAtUtc: '2026-11-06T10:00:00Z',
  };
}

function response(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const fetchMock = vi.fn<typeof fetch>();

afterEach(() => {
  cleanup();
  fetchMock.mockReset();
  vi.unstubAllGlobals();
});

describe('керування webhook UkraineAlarm', () => {
  it('вимагає звірити невідомий стан до першої реєстрації', async () => {
    const user = userEvent.setup();
    let registration: 'registered' | 'not_registered' | 'unknown' = 'unknown';
    fetchMock.mockImplementation(async (input, init) => {
      const url = String(input);
      if (url === `${path}/state`) return response(state(registration));
      if (url === '/api/v1/auth/csrf-token') return response({ token: 'csrf-token' });
      if (url === `${path}/state/reconcile`) {
        expect(JSON.parse(String(init?.body))).toEqual({ confirmed: true, isRegistered: false });
        registration = 'not_registered';
        return response(state(registration));
      }
      return response({}, 404);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<UkraineAlarmWebhookPanel />);
    expect(await screen.findByText('Стан потрібно звірити')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Зареєструвати webhook' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'Не зареєстрований' }));
    await user.click(screen.getByRole('button', { name: 'Звірити стан реєстрації' }));

    expect(await screen.findByText('Webhook не зареєстрований')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Зареєструвати webhook' })).toBeInTheDocument();
  });

  it('реєструє webhook із CSRF та ключем повтору, а потім оновлює стан', async () => {
    const user = userEvent.setup();
    let registered = false;
    fetchMock.mockImplementation(async (input, init) => {
      const url = String(input);
      if (url === `${path}/state`)
        return response(state(registered ? 'registered' : 'not_registered'));
      if (url === '/api/v1/auth/csrf-token') return response({ token: 'csrf-token' });
      if (url === path && init?.method === 'POST') {
        expect(init.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf-token' });
        expect(init.headers).toHaveProperty('Idempotency-Key');
        expect(JSON.parse(String(init.body))).toEqual({ action: 'register' });
        registered = true;
        return response(operation('Succeeded'), 200);
      }
      return response({}, 404);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<UkraineAlarmWebhookPanel />);
    await user.click(await screen.findByRole('button', { name: 'Зареєструвати webhook' }));

    expect(await screen.findByText('Webhook зареєстрований')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      path,
      expect.objectContaining({ method: 'POST', credentials: 'same-origin', cache: 'no-store' }),
    );
  });

  it('після перезавантаження відновлює невідому операцію та дозволяє її звірити', async () => {
    const user = userEvent.setup();
    let resolved = false;
    fetchMock.mockImplementation(async (input, init) => {
      const url = String(input);
      if (url === `${path}/state`) {
        return response(resolved ? state('registered') : state('unknown', operation('Unknown')));
      }
      if (url === '/api/v1/auth/csrf-token') return response({ token: 'csrf-token' });
      if (url === `${path}/operation-1/resolve`) {
        expect(JSON.parse(String(init?.body))).toEqual({ confirmed: true, isRegistered: true });
        resolved = true;
        return response(operation('Resolved'));
      }
      return response({}, 404);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<UkraineAlarmWebhookPanel />);
    expect(
      await screen.findByText('Результат невідомий — звірте стан у кабінеті'),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Callback URL' })).toHaveAttribute(
      'href',
      'https://alerts.example.test/api/v1/alert-sources/ukrainealarm/webhook',
    );

    await user.click(screen.getByRole('radio', { name: 'Зареєстрований' }));
    await user.click(screen.getByRole('button', { name: 'Підтвердити результат операції' }));

    expect(await screen.findByText('Webhook зареєстрований')).toBeInTheDocument();
  });

  it('не пропонує дії, поки операція очікує або виконується', async () => {
    fetchMock.mockResolvedValueOnce(response(state('unknown', operation('Running'))));
    vi.stubGlobal('fetch', fetchMock);

    render(<UkraineAlarmWebhookPanel />);

    expect(await screen.findByText('Провайдер обробляє запит')).toBeInTheDocument();
    expect(screen.getByText(/Дочекайтеся завершення операції/)).toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Зареєструвати webhook' })).not.toBeInTheDocument();
  });
});
