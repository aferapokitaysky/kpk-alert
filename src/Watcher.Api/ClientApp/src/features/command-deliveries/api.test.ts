import { afterEach, describe, expect, it, vi } from 'vitest';
import { CommandDeliveriesApiError, getCommandDeliveries } from './api';

const delivery = {
  deviceId: 'device-1',
  deviceName: 'Вузол головного корпусу',
  state: 'Accepted',
  createdAtUtc: '2026-11-05T10:00:00Z',
  expiresAtUtc: '2026-11-05T10:15:00Z',
  lastAttemptAtUtc: null,
  attemptCount: 0,
};

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

describe('клієнт доставлення команд', () => {
  it('запитує поточну сторінку без кешування та повертає перевірені дані', async () => {
    fetchMock.mockResolvedValueOnce(
      response(200, {
        items: [delivery],
        page: 2,
        pageSize: 25,
        totalCount: 26,
        hasUnfinishedDeliveries: true,
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await expect(getCommandDeliveries('command-1', 2)).resolves.toEqual({
      items: [delivery],
      page: 2,
      pageSize: 25,
      totalCount: 26,
      hasUnfinishedDeliveries: true,
    });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/alert-commands/command-1/deliveries?page=2&pageSize=25',
      expect.objectContaining({
        method: 'GET',
        cache: 'no-store',
        credentials: 'same-origin',
        headers: { Accept: 'application/json' },
      }),
    );
  });

  it('відхиляє невідомий статус замість того, щоб вигадувати його значення', async () => {
    fetchMock.mockResolvedValueOnce(
      response(200, {
        items: [{ ...delivery, state: 'Queued' }],
        page: 1,
        pageSize: 25,
        totalCount: 1,
        hasUnfinishedDeliveries: false,
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await expect(getCommandDeliveries('command-1', 1)).rejects.toThrow(
      'invalid-command-deliveries-response',
    );
  });

  it('передає HTTP-помилку компоненту без підміни відповіді порожнім списком', async () => {
    fetchMock.mockResolvedValueOnce(response(404));
    vi.stubGlobal('fetch', fetchMock);

    const request = getCommandDeliveries('command-1', 1);
    await expect(request).rejects.toBeInstanceOf(CommandDeliveriesApiError);
    await expect(request).rejects.toMatchObject({ status: 404 });
  });
});
