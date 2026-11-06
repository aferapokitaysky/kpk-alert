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
            deviceId: 'device-01',
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
      deviceId: 'device-01',
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
          deviceId: 'device-01',
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

  it('реєструє вузол через CSRF-захищений API та оновлює список', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation(async (input, init) => {
      const url = String(input);
      if (url === '/api/v1/locations') {
        return jsonResponse([{ id: 'location-01', name: 'Північний корпус', code: 'north' }]);
      }
      if (url === '/api/v1/auth/csrf-token') return jsonResponse({ token: 'csrf-value' });
      if (url === '/api/v1/admin/devices' && init?.method === 'POST') {
        expect(init.headers).toMatchObject({
          'X-CSRF-TOKEN': 'csrf-value',
          'Content-Type': 'application/json',
        });
        expect(JSON.parse(String(init.body))).toEqual({
          name: 'Вузол 02',
          locationId: 'location-01',
          certificateSha256: 'a'.repeat(64),
        });
        return jsonResponse(
          {
            deviceId: 'device-02',
            name: 'Вузол 02',
            locationId: 'location-01',
            isActive: true,
          },
          201,
        );
      }
      if (url.startsWith('/api/v1/admin/devices?')) {
        return jsonResponse({ items: [], page: 1, pageSize: 25, totalCount: 0 });
      }
      throw new Error(`Unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<DeviceStatusPage canManage />);
    fireEvent.change(await screen.findByLabelText('Назва вузла'), {
      target: { value: 'Вузол 02' },
    });
    fireEvent.change(screen.getByLabelText('Корпус'), { target: { value: 'location-01' } });
    fireEvent.change(screen.getByLabelText('SHA-256 відбиток сертифіката'), {
      target: { value: 'a'.repeat(64) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Зареєструвати пристрій' }));

    expect(await screen.findByText('Пристрій зареєстровано.')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/admin/devices',
      expect.objectContaining({
        method: 'POST',
        cache: 'no-store',
        credentials: 'same-origin',
      }),
    );
  });

  it('просить підтвердження перед блокуванням і відображає закритий доступ', async () => {
    const device = {
      deviceId: 'device-01',
      name: 'Вузол 01',
      locationName: 'Північний корпус',
      locationIsActive: true,
      isActive: true,
      isConnected: true,
      lastSeenAtUtc: null,
    };
    let revoked = false;
    const fetchMock = vi.fn<typeof fetch>().mockImplementation(async (input, init) => {
      const url = String(input);
      if (url === '/api/v1/locations') return jsonResponse([]);
      if (url === '/api/v1/auth/csrf-token') return jsonResponse({ token: 'csrf-value' });
      if (url === '/api/v1/admin/devices/device-01/revoke') {
        expect(init?.method).toBe('POST');
        expect(init?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf-value' });
        revoked = true;
        return jsonResponse({
          deviceId: 'device-01',
          isActive: false,
          revokedAtUtc: '2026-11-07T07:00:00Z',
        });
      }
      if (url.startsWith('/api/v1/admin/devices?')) {
        return jsonResponse({
          items: [{ ...device, isActive: !revoked, isConnected: !revoked }],
          page: 1,
          pageSize: 25,
          totalCount: 1,
        });
      }
      throw new Error(`Unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<DeviceStatusPage canManage />);
    fireEvent.click(
      await screen.findByRole('button', { name: 'Заблокувати доступ пристрою «Вузол 01»' }),
    );
    expect(screen.getByRole('button', { name: 'Підтвердити блокування' })).toHaveFocus();
    expect(
      screen.getByText('Вузол одразу втратить доступ до сервера. Цю дію не можна скасувати.'),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Скасувати' }));
    const revokeButton = screen.getByRole('button', {
      name: 'Заблокувати доступ пристрою «Вузол 01»',
    });
    expect(revokeButton).toHaveFocus();
    fireEvent.click(revokeButton);
    fireEvent.click(screen.getByRole('button', { name: 'Підтвердити блокування' }));

    expect(await screen.findByText('Доступ пристрою «Вузол 01» заблоковано.')).toBeInTheDocument();
    expect(await screen.findByText('Доступ заблоковано')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Заблокувати доступ пристрою «Вузол 01»' }),
    ).not.toBeInTheDocument();
  });

  it('завершує локальну сесію, якщо CSRF endpoint повернув 401', async () => {
    const onSessionExpired = vi.fn();
    const fetchMock = vi.fn<typeof fetch>().mockImplementation(async (input) => {
      const url = String(input);
      if (url === '/api/v1/locations') {
        return jsonResponse([{ id: 'location-01', name: 'Північний корпус', code: 'north' }]);
      }
      if (url.startsWith('/api/v1/admin/devices?')) {
        return jsonResponse({ items: [], page: 1, pageSize: 25, totalCount: 0 });
      }
      if (url === '/api/v1/auth/csrf-token') return jsonResponse({}, 401);
      throw new Error(`Unexpected request: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<DeviceStatusPage canManage onSessionExpired={onSessionExpired} />);
    fireEvent.change(await screen.findByLabelText('Назва вузла'), {
      target: { value: 'Вузол 02' },
    });
    fireEvent.change(screen.getByLabelText('Корпус'), { target: { value: 'location-01' } });
    fireEvent.change(screen.getByLabelText('SHA-256 відбиток сертифіката'), {
      target: { value: 'a'.repeat(64) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Зареєструвати пристрій' }));

    expect(
      await screen.findByText('Сесію завершено. Увійдіть знову, щоб продовжити.'),
    ).toBeInTheDocument();
    expect(onSessionExpired).toHaveBeenCalledOnce();
    expect(fetchMock).not.toHaveBeenCalledWith(
      '/api/v1/admin/devices',
      expect.objectContaining({ method: 'POST' }),
    );
  });
});
