import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DeviceStatusPage } from './DeviceStatusPage';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('сторінка стану пристроїв', () => {
  it('розрізняє підключення до сервера та збережений час активності', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValueOnce(
      jsonResponse({
        items: [
          {
            name: 'Вузол 01',
            locationName: 'Північний корпус',
            locationIsActive: true,
            isActive: true,
            isConnected: false,
            lastSeenAtUtc: '2026-11-06T08:05:00Z',
          },
        ],
        page: 1,
        pageSize: 25,
        totalCount: 1,
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<DeviceStatusPage />);

    expect(await screen.findByText('Вузол 01')).toBeInTheDocument();
    expect(screen.getByText('Немає з’єднання')).toBeInTheDocument();
    expect(screen.getByText('Остання збережена активність')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/admin/devices?page=1&pageSize=25',
      expect.objectContaining({ cache: 'no-store', credentials: 'same-origin' }),
    );
  });

  it('показує окреме повідомлення для порожнього списку', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn<typeof fetch>()
        .mockResolvedValueOnce(jsonResponse({ items: [], page: 1, pageSize: 25, totalCount: 0 })),
    );
    render(<DeviceStatusPage />);
    expect(await screen.findByText('Зареєстрованих пристроїв поки немає.')).toBeInTheDocument();
  });

  it('повертається до останньої сторінки, якщо список зменшився', async () => {
    const device = {
      name: 'Вузол 01',
      locationName: 'Північний корпус',
      locationIsActive: true,
      isActive: true,
      isConnected: false,
      lastSeenAtUtc: null,
    };
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(
        jsonResponse({ items: [device], page: 1, pageSize: 25, totalCount: 26 }),
      )
      .mockResolvedValueOnce(jsonResponse({ items: [], page: 2, pageSize: 25, totalCount: 1 }))
      .mockResolvedValueOnce(
        jsonResponse({ items: [device], page: 1, pageSize: 25, totalCount: 1 }),
      );
    vi.stubGlobal('fetch', fetchMock);

    render(<DeviceStatusPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Наступна' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    expect(await screen.findByText('Пристроїв: 1')).toBeInTheDocument();
    expect(
      screen.queryByRole('navigation', { name: 'Сторінки пристроїв' }),
    ).not.toBeInTheDocument();
  });

  it('оновлює стан кожні 15 секунд, призупиняється у прихованій вкладці й скасовує запит', async () => {
    vi.useFakeTimers();
    const originalHiddenDescriptor = Object.getOwnPropertyDescriptor(document, 'hidden');
    const setHidden = (hidden: boolean) =>
      Object.defineProperty(document, 'hidden', { configurable: true, value: hidden });
    setHidden(false);
    const page = {
      items: [
        {
          name: 'Вузол 01',
          locationName: 'Північний корпус',
          locationIsActive: true,
          isActive: true,
          isConnected: true,
          lastSeenAtUtc: null,
        },
      ],
      page: 1,
      pageSize: 25,
      totalCount: 1,
    };
    const signals: AbortSignal[] = [];
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse(page))
      .mockImplementationOnce((_input, init) => {
        const signal = init?.signal;
        if (signal) signals.push(signal);
        return new Promise<Response>((_resolve, reject) => {
          signal?.addEventListener(
            'abort',
            () => reject(new DOMException('Запит скасовано', 'AbortError')),
            { once: true },
          );
        });
      })
      .mockResolvedValueOnce(jsonResponse(page));
    vi.stubGlobal('fetch', fetchMock);

    const { unmount } = render(<DeviceStatusPage />);
    await act(async () => {
      await Promise.resolve();
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(15_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);

    await act(async () => {
      setHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(signals[0]?.aborted).toBe(true);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);

    await act(async () => {
      setHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(fetchMock).toHaveBeenCalledTimes(3);
    unmount();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(3);
    if (originalHiddenDescriptor) {
      Object.defineProperty(document, 'hidden', originalHiddenDescriptor);
    } else {
      Reflect.deleteProperty(document, 'hidden');
    }
  });
});
