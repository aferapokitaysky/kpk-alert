import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  createAlertCommand,
  createStopCommand,
  getAlertCommand,
  getAuditEvents,
  getSession,
  logout,
} from './api';

function response(status: number, body?: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

const fetchMock = vi.fn<typeof fetch>();

afterEach(() => {
  fetchMock.mockReset();
  vi.unstubAllGlobals();
});

describe('контракти клієнта', () => {
  it('читає ролі й можливості із захищеного стану сесії', async () => {
    fetchMock.mockResolvedValueOnce(
      response(200, {
        authenticated: true,
        roles: ['notificator', 'auditor'],
        capabilities: { canOperate: true, canViewAudit: true },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await expect(getSession()).resolves.toEqual({
      authenticated: true,
      roles: ['notificator', 'auditor'],
      capabilities: { canOperate: true, canViewAudit: true },
    });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/auth/session',
      expect.objectContaining({
        cache: 'no-store',
        credentials: 'same-origin',
      }),
    );
  });

  it('передає CSRF та ідемпотентність і приймає лише створену команду', async () => {
    const command = {
      id: 'command-id',
      action: 'start',
      presetCode: 'evacuation',
      targetCommandId: null,
      createdAtUtc: '2026-11-05T12:00:00Z',
      targets: [{ locationId: 'location-id', state: 'Created' }],
    };
    fetchMock
      .mockResolvedValueOnce(response(200, { token: 'request-token' }))
      .mockResolvedValueOnce(response(202, command));
    vi.stubGlobal('fetch', fetchMock);

    await expect(
      createAlertCommand('evacuation', ['location-id'], 'idempotency-id'),
    ).resolves.toEqual(command);
    const [url, init] = fetchMock.mock.calls[1] ?? [];
    expect(url).toBe('/api/v1/alert-commands');
    expect(init?.method).toBe('POST');
    expect(init?.credentials).toBe('same-origin');
    expect(init?.cache).toBe('no-store');
    expect(init?.headers).toMatchObject({
      'X-CSRF-TOKEN': 'request-token',
      'Idempotency-Key': 'idempotency-id',
    });
    expect(JSON.parse(String(init?.body))).toEqual({
      action: 'start',
      presetCode: 'evacuation',
      locationIds: ['location-id'],
    });
  });

  it('передає id цільового запуску та корпуси для команди відбою', async () => {
    const command = {
      id: 'stop-id',
      action: 'stop',
      presetCode: null,
      targetCommandId: 'start-id',
      createdAtUtc: '2026-11-05T12:01:00Z',
      targets: [{ locationId: 'location-id', state: 'Created' }],
    };
    fetchMock
      .mockResolvedValueOnce(response(200, { token: 'request-token' }))
      .mockResolvedValueOnce(response(202, command));
    vi.stubGlobal('fetch', fetchMock);

    await expect(createStopCommand('start-id', ['location-id'], 'stop-key')).resolves.toEqual(
      command,
    );
    const [url, init] = fetchMock.mock.calls[1] ?? [];
    expect(url).toBe('/api/v1/alert-commands/start-id/stop');
    expect(init?.headers).toMatchObject({ 'Idempotency-Key': 'stop-key' });
    expect(JSON.parse(String(init?.body))).toEqual({
      locationIds: ['location-id'],
    });
  });

  it('відновлює команду лише з повної серверної відповіді', async () => {
    const commandId = '11111111-1111-4111-8111-111111111111';
    const command = {
      id: commandId,
      action: 'start',
      presetCode: 'evacuation',
      targetCommandId: null,
      createdAtUtc: '2026-11-05T12:00:00Z',
      expiresAtUtc: '2026-11-05T12:05:00Z',
      targets: [{ locationId: 'location-id', state: 'Created' }],
    };
    fetchMock.mockResolvedValueOnce(response(200, command));
    vi.stubGlobal('fetch', fetchMock);

    await expect(getAlertCommand(commandId)).resolves.toEqual({
      id: commandId,
      action: 'start',
      presetCode: 'evacuation',
      targetCommandId: null,
      createdAtUtc: command.createdAtUtc,
      targets: command.targets,
    });
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/v1/alert-commands/${commandId}`,
      expect.objectContaining({ cache: 'no-store', credentials: 'same-origin' }),
    );
  });

  it('відновлює команду зупинки з повної серверної відповіді', async () => {
    const commandId = '22222222-2222-4222-8222-222222222222';
    const command = {
      id: commandId,
      action: 'stop',
      presetCode: null,
      targetCommandId: '11111111-1111-4111-8111-111111111111',
      createdAtUtc: '2026-11-05T12:01:00Z',
      expiresAtUtc: '2026-11-05T12:06:00Z',
      targets: [{ locationId: 'location-id', state: 'Created' }],
    };
    fetchMock.mockResolvedValueOnce(response(200, command));
    vi.stubGlobal('fetch', fetchMock);

    await expect(getAlertCommand(commandId)).resolves.toMatchObject({
      id: commandId,
      action: 'stop',
      presetCode: null,
      targetCommandId: command.targetCommandId,
    });
  });

  it('відхиляє невідому дію у відповіді сервера', async () => {
    fetchMock.mockResolvedValueOnce(
      response(200, {
        id: '11111111-1111-4111-8111-111111111111',
        action: 'pause',
        presetCode: null,
        createdAtUtc: '2026-11-05T12:00:00Z',
        targets: [],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await expect(getAlertCommand('11111111-1111-4111-8111-111111111111')).rejects.toThrow(
      'invalid-command-response',
    );
  });

  it('передає серверні фільтр і пагінацію журналу без довільних параметрів', async () => {
    const page = { items: [], page: 2, pageSize: 25, totalCount: 26 };
    fetchMock.mockResolvedValueOnce(response(200, page));
    vi.stubGlobal('fetch', fetchMock);

    await expect(getAuditEvents(2, 25, 'auth.logout')).resolves.toEqual(page);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/audit-events?page=2&pageSize=25&eventType=auth.logout',
      expect.objectContaining({ cache: 'no-store' }),
    );
  });

  it('передає actor і UTC-межі до API журналу', async () => {
    const page = { items: [], page: 1, pageSize: 25, totalCount: 0 };
    fetchMock.mockResolvedValueOnce(response(200, page));
    vi.stubGlobal('fetch', fetchMock);

    await expect(
      getAuditEvents(1, 25, '', {
        actorId: 'c6b2f302-0675-4a4d-88eb-cf18c9022920',
        fromUtc: '2026-11-05T07:00:00.000Z',
        toUtc: '2026-11-05T08:00:00.000Z',
      }),
    ).resolves.toEqual(page);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/audit-events?page=1&pageSize=25&actorId=c6b2f302-0675-4a4d-88eb-cf18c9022920&fromUtc=2026-11-05T07%3A00%3A00.000Z&toUtc=2026-11-05T08%3A00%3A00.000Z',
      expect.objectContaining({ cache: 'no-store' }),
    );
  });

  it('не приховує відмову сервера під час завершення сесії', async () => {
    fetchMock
      .mockResolvedValueOnce(response(200, { token: 'request-token' }))
      .mockResolvedValueOnce(response(403));
    vi.stubGlobal('fetch', fetchMock);

    await expect(logout()).rejects.toMatchObject({ status: 403 });
  });
});
