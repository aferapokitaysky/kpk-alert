import { afterEach, describe, expect, it, vi } from 'vitest';
import { getMinuteOfSilenceStatus, MinuteOfSilenceApiError } from './api';

const baseStatus = {
  runDate: '2026-11-06',
  timeZone: 'Europe/Kyiv',
  scheduledAtUtc: '2026-11-06T07:00:00Z',
  outcome: null,
  skipReason: null,
  commandId: null,
  processedAtUtc: null,
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const fetchMock = vi.fn<typeof fetch>();

afterEach(() => {
  fetchMock.mockReset();
  vi.unstubAllGlobals();
});

describe('API статусу хвилини мовчання', () => {
  it('запитує стан без кешування та приймає ще не оброблений розклад', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(baseStatus));
    vi.stubGlobal('fetch', fetchMock);

    await expect(getMinuteOfSilenceStatus()).resolves.toEqual(baseStatus);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/minute-of-silence/status/today',
      expect.objectContaining({ cache: 'no-store', credentials: 'same-origin' }),
    );
  });

  it.each(['queued', 'skipped'] as const)('приймає результат %s', async (outcome) => {
    const status = {
      ...baseStatus,
      outcome,
      skipReason: outcome === 'skipped' ? 'missed_window' : null,
      commandId: outcome === 'queued' ? '4d97e834-37b1-4ff9-936d-90b547ab36f1' : null,
      processedAtUtc: '2026-11-06T07:02:00Z',
    };
    fetchMock.mockResolvedValueOnce(jsonResponse(status));
    vi.stubGlobal('fetch', fetchMock);

    await expect(getMinuteOfSilenceStatus()).resolves.toEqual(status);
  });

  it('передає AbortSignal запиту', async () => {
    const controller = new AbortController();
    fetchMock.mockResolvedValueOnce(jsonResponse(baseStatus));
    vi.stubGlobal('fetch', fetchMock);

    await getMinuteOfSilenceStatus(controller.signal);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/minute-of-silence/status/today',
      expect.objectContaining({ signal: controller.signal }),
    );
  });

  it('повідомляє HTTP-помилку без тіла відповіді', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 403));
    vi.stubGlobal('fetch', fetchMock);

    await expect(getMinuteOfSilenceStatus()).rejects.toBeInstanceOf(MinuteOfSilenceApiError);
  });

  it('відхиляє невідомий стан, дату або часовий пояс', async () => {
    const invalidStatuses = [
      { ...baseStatus, outcome: 'processing' },
      { ...baseStatus, runDate: '2026-02-30' },
      { ...baseStatus, timeZone: 'UTC' },
    ];

    for (const status of invalidStatuses) {
      fetchMock.mockResolvedValueOnce(jsonResponse(status));
      vi.stubGlobal('fetch', fetchMock);
      await expect(getMinuteOfSilenceStatus()).rejects.toThrow(
        'invalid-minute-of-silence-status-response',
      );
    }
  });
});
