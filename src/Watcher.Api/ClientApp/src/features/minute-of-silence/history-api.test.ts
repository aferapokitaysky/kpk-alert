import { afterEach, describe, expect, it, vi } from 'vitest';
import { getMinuteOfSilenceHistory, MinuteOfSilenceHistoryApiError } from './history-api';

const entry = {
  runDate: '2026-11-06',
  outcome: 'skipped',
  skipReason: 'missed_window',
  processedAtUtc: '2026-11-06T07:02:00Z',
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

describe('API історії хвилини мовчання', () => {
  it('запитує заданий період без кешування та перевіряє сторінку', async () => {
    const page = {
      items: [entry],
      from: '2026-11-01',
      to: '2026-11-06',
      page: 2,
      pageSize: 10,
      totalCount: 12,
    };
    fetchMock.mockResolvedValueOnce(jsonResponse(page));
    vi.stubGlobal('fetch', fetchMock);

    await expect(getMinuteOfSilenceHistory('2026-11-01', '2026-11-06', 2, 10)).resolves.toEqual(
      page,
    );
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/minute-of-silence/history?from=2026-11-01&to=2026-11-06&page=2&pageSize=10',
      expect.objectContaining({ cache: 'no-store', credentials: 'same-origin' }),
    );
  });

  it('передає сигнал скасування', async () => {
    const controller = new AbortController();
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        items: [],
        from: '2026-11-01',
        to: '2026-11-06',
        page: 1,
        pageSize: 25,
        totalCount: 0,
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await getMinuteOfSilenceHistory('2026-11-01', '2026-11-06', 1, 25, controller.signal);

    expect(fetchMock).toHaveBeenCalledWith(
      expect.any(String),
      expect.objectContaining({ signal: controller.signal }),
    );
  });

  it('повертає помилку HTTP і відхиляє некоректну відповідь', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 403));
    vi.stubGlobal('fetch', fetchMock);
    await expect(
      getMinuteOfSilenceHistory('2026-11-01', '2026-11-06', 1, 25),
    ).rejects.toBeInstanceOf(MinuteOfSilenceHistoryApiError);

    fetchMock.mockResolvedValueOnce(jsonResponse({ items: [entry], from: '2026-11-01' }));
    await expect(getMinuteOfSilenceHistory('2026-11-01', '2026-11-06', 1, 25)).rejects.toThrow(
      'invalid-minute-of-silence-history-response',
    );
  });

  it('відхиляє стан поза контрактом історії', async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        items: [{ ...entry, outcome: 'playing' }],
        from: '2026-11-01',
        to: '2026-11-06',
        page: 1,
        pageSize: 25,
        totalCount: 1,
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await expect(getMinuteOfSilenceHistory('2026-11-01', '2026-11-06', 1, 25)).rejects.toThrow(
      'invalid-minute-of-silence-history-response',
    );
  });
});
