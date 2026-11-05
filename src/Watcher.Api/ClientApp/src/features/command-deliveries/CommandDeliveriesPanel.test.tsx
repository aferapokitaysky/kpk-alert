import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CommandDeliveriesPanel } from './CommandDeliveriesPanel';

const accepted = {
  deviceId: 'device-1',
  deviceName: 'Вузол головного корпусу',
  state: 'Accepted',
  createdAtUtc: '2026-11-05T10:00:00Z',
  expiresAtUtc: '2026-11-05T10:15:00Z',
  lastAttemptAtUtc: '2026-11-05T10:01:00Z',
  attemptCount: 1,
};
const playing = {
  ...accepted,
  deviceId: 'device-2',
  deviceName: 'Вузол спортивного корпусу',
  state: 'Playing',
  lastAttemptAtUtc: null,
  attemptCount: 0,
};

function page(items: unknown[], currentPage = 1, totalCount = items.length) {
  return { items, page: currentPage, pageSize: 25, totalCount };
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const fetchMock = vi.fn<typeof fetch>();
const originalHiddenDescriptor = Object.getOwnPropertyDescriptor(document, 'hidden');

function setDocumentHidden(hidden: boolean) {
  Object.defineProperty(document, 'hidden', { configurable: true, value: hidden });
}

afterEach(() => {
  cleanup();
  fetchMock.mockReset();
  vi.useRealTimers();
  if (originalHiddenDescriptor) {
    Object.defineProperty(document, 'hidden', originalHiddenDescriptor);
  } else {
    Reflect.deleteProperty(document, 'hidden');
  }
  vi.unstubAllGlobals();
});

describe('панель доставлення команди', () => {
  it('пояснює різницю між прийняттям команди та відтворенням звуку', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(page([accepted, playing])));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);

    const panel = screen.getByRole('region', { name: 'Доставка команди' });
    expect(await within(panel).findByText('Вузол прийняв команду')).toBeInTheDocument();
    expect(within(panel).getByText('Звук відтворюється')).toBeInTheDocument();
    expect(
      within(panel).getByText('Прийняття команди не означає, що звук уже відтворюється.'),
    ).toBeInTheDocument();
    expect(within(panel).getByText('Ще не було')).toBeInTheDocument();
  });

  it('показує завантаження та окремий порожній стан', async () => {
    let resolveResponse: ((response: Response) => void) | undefined;
    fetchMock.mockReturnValueOnce(
      new Promise((resolve) => {
        resolveResponse = resolve;
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    const panel = screen.getByRole('region', { name: 'Доставка команди' });
    expect(panel).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('status')).toHaveTextContent('Завантажуємо статуси доставки');

    resolveResponse?.(jsonResponse(page([])));
    await waitFor(() =>
      expect(screen.getByRole('status')).toHaveTextContent(
        'Для цієї команди поки немає записів доставки.',
      ),
    );
    expect(panel).toHaveAttribute('aria-busy', 'false');
  });

  it('дає повторити запит після помилки сервера', async () => {
    const user = userEvent.setup();
    fetchMock
      .mockResolvedValueOnce(jsonResponse({}, 503))
      .mockResolvedValueOnce(jsonResponse(page([accepted])));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Не вдалося завантажити статуси доставки',
    );

    await user.click(screen.getByRole('button', { name: 'Повторити' }));
    expect(await screen.findByText('Вузол прийняв команду')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('оновлює статус вручну до наступного інтервалу', async () => {
    const user = userEvent.setup();
    fetchMock
      .mockResolvedValueOnce(jsonResponse(page([accepted])))
      .mockResolvedValueOnce(jsonResponse(page([playing])));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    expect(await screen.findByText('Вузол прийняв команду')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole('button', { name: 'Оновити' }));
    expect(await screen.findByText('Звук відтворюється')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('оновлює статус кожні 15 секунд і не запускає паралельний запит', async () => {
    vi.useFakeTimers();
    setDocumentHidden(false);
    let resolveSecondRequest: ((response: Response) => void) | undefined;
    fetchMock
      .mockResolvedValueOnce(jsonResponse(page([accepted])))
      .mockReturnValueOnce(
        new Promise((resolve) => {
          resolveSecondRequest = resolve;
        }),
      )
      .mockResolvedValueOnce(jsonResponse(page([playing])));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => {
      await Promise.resolve();
    });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(15_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);

    resolveSecondRequest?.(jsonResponse(page([playing])));
    await act(async () => {
      await Promise.resolve();
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(15_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(screen.getByText('Звук відтворюється')).toBeInTheDocument();
  });

  it('призупиняє запити у прихованій вкладці та оновлює одразу після повернення', async () => {
    vi.useFakeTimers();
    setDocumentHidden(false);
    const requestSignals: AbortSignal[] = [];
    fetchMock
      .mockImplementationOnce((_input, init) => {
        const signal = init?.signal;
        if (signal) requestSignals.push(signal);
        return new Promise<Response>((_resolve, reject) => {
          signal?.addEventListener(
            'abort',
            () => reject(new DOMException('Запит скасовано', 'AbortError')),
            { once: true },
          );
        });
      })
      .mockResolvedValueOnce(jsonResponse(page([playing])));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await act(async () => {
      setDocumentHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(requestSignals[0]?.aborted).toBe(true);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await act(async () => {
      setDocumentHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
      await Promise.resolve();
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(screen.getByText('Звук відтворюється')).toBeInTheDocument();
  });

  it('скасовує запит при зміні commandId та під час unmount', async () => {
    vi.useFakeTimers();
    setDocumentHidden(false);
    const requestSignals: AbortSignal[] = [];
    const fetchMockImplementation: typeof fetch = (_input, init) => {
      const signal = init?.signal;
      if (signal) requestSignals.push(signal);
      return new Promise<Response>((_resolve, reject) => {
        signal?.addEventListener(
          'abort',
          () => reject(new DOMException('Запит скасовано', 'AbortError')),
          { once: true },
        );
      });
    };
    fetchMock.mockImplementation(fetchMockImplementation);
    vi.stubGlobal('fetch', fetchMock);

    const { rerender, unmount } = render(<CommandDeliveriesPanel commandId="command-1" />);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    rerender(<CommandDeliveriesPanel commandId="command-2" />);
    expect(requestSignals[0]?.aborted).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock).toHaveBeenLastCalledWith(
      '/api/v1/alert-commands/command-2/deliveries?page=1&pageSize=25',
      expect.objectContaining({ signal: requestSignals[1] }),
    );

    unmount();
    expect(requestSignals[1]?.aborted).toBe(true);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('показує помилку автоматичного оновлення та залишає повторну спробу доступною', async () => {
    vi.useFakeTimers();
    setDocumentHidden(false);
    fetchMock
      .mockResolvedValueOnce(jsonResponse(page([accepted])))
      .mockResolvedValueOnce(jsonResponse({}, 503))
      .mockResolvedValueOnce(jsonResponse(page([playing])));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    await act(async () => {
      await Promise.resolve();
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(15_000);
    });

    expect(screen.getByRole('alert')).toHaveTextContent('Не вдалося завантажити статуси доставки');
    expect(screen.getByText('Вузол прийняв команду')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Повторити' }));
    await act(async () => {
      await Promise.resolve();
    });
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(screen.getByText('Звук відтворюється')).toBeInTheDocument();
  });

  it('перемикає сторінки в межах відповіді сервера', async () => {
    const user = userEvent.setup();
    fetchMock
      .mockResolvedValueOnce(jsonResponse(page([accepted], 1, 26)))
      .mockResolvedValueOnce(jsonResponse(page([playing], 2, 26)))
      .mockResolvedValueOnce(jsonResponse(page([accepted], 1, 26)));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    expect(await screen.findByText('Сторінка 1 з 2')).toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'Статуси доставки за вузлами' })).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Сторінки доставки' })).toBeInTheDocument();
    expect(screen.getByText('Сторінка 1 з 2')).toHaveAttribute('aria-live', 'polite');
    await user.click(screen.getByRole('button', { name: 'Наступна' }));

    expect(await screen.findByText('Сторінка 2 з 2')).toBeInTheDocument();
    expect(screen.getByText('Звук відтворюється')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenLastCalledWith(
      '/api/v1/alert-commands/command-1/deliveries?page=2&pageSize=25',
      expect.objectContaining({ cache: 'no-store' }),
    );
    await user.click(screen.getByRole('button', { name: 'Попередня' }));
    expect(await screen.findByText('Сторінка 1 з 2')).toBeInTheDocument();
  });

  it('зберігає клавіатурний фокус під час переходу між сторінками', async () => {
    const user = userEvent.setup();
    let resolveSecondPage: ((response: Response) => void) | undefined;
    fetchMock.mockResolvedValueOnce(jsonResponse(page([accepted], 1, 26))).mockReturnValueOnce(
      new Promise((resolve) => {
        resolveSecondPage = resolve;
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    expect(await screen.findByText('Сторінка 1 з 2')).toBeInTheDocument();

    await user.tab();
    await user.tab();
    await user.tab();
    const nextButton = screen.getByRole('button', { name: 'Наступна' });
    expect(nextButton).toHaveFocus();
    await user.keyboard('{Enter}');

    expect(screen.getByRole('status')).toHaveTextContent('Оновлюємо статуси доставки');
    expect(nextButton).toHaveAttribute('aria-disabled', 'true');
    expect(nextButton).toHaveFocus();

    resolveSecondPage?.(jsonResponse(page([playing], 2, 26)));
    expect(await screen.findByText('Сторінка 2 з 2')).toBeInTheDocument();
    expect(nextButton).toHaveFocus();
  });

  it('зберігає фокус на повторі запиту до завершення завантаження', async () => {
    const user = userEvent.setup();
    let resolveRetry: ((response: Response) => void) | undefined;
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 503)).mockReturnValueOnce(
      new Promise((resolve) => {
        resolveRetry = resolve;
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Не вдалося завантажити статуси доставки',
    );
    const retryButton = screen.getByRole('button', { name: 'Повторити' });
    await user.click(retryButton);

    expect(retryButton).toHaveFocus();
    expect(retryButton).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('alert')).toBeInTheDocument();

    resolveRetry?.(jsonResponse(page([accepted])));
    expect(await screen.findByText('Вузол прийняв команду')).toBeInTheDocument();
    expect(retryButton).toHaveFocus();
  });

  it('повідомляє про відсутність доступу без технічної відповіді сервера', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 403));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'У вас немає доступу до стану доставки цієї команди.',
    );
  });

  it('не розкриває, чи існує команда, якщо сервер повернув 404', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 404));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Команду не знайдено або немає доступу до її стану доставки.',
    );
  });

  it('повідомляє, коли сервер завершив сесію', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 401));
    vi.stubGlobal('fetch', fetchMock);

    render(<CommandDeliveriesPanel commandId="command-1" />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Сесію завершено. Увійдіть знову, щоб продовжити.',
    );
  });
});
