import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { SessionState } from '../../state';
import { MinuteOfSilenceStatusPanel } from './MinuteOfSilenceStatusPanel';

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

function session(roles: string[]): SessionState {
  return {
    status: 'authenticated',
    roles,
    capabilities: {
      canOperate: roles.includes('notificator'),
      canViewAudit: roles.includes('admin') || roles.includes('auditor'),
    },
  };
}

function sessionWithAuditCapability(canViewAudit: boolean, roles: string[] = []): SessionState {
  return {
    status: 'authenticated',
    roles,
    capabilities: {
      canOperate: roles.includes('notificator'),
      canViewAudit,
    },
  };
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

describe('панель статусу хвилини мовчання', () => {
  it('показує розклад 09:00 за Києвом і не прирівнює чергу до відтворення', async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        ...baseStatus,
        outcome: 'queued',
        commandId: '4d97e834-37b1-4ff9-936d-90b547ab36f1',
        processedAtUtc: '2026-11-06T07:02:00Z',
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<MinuteOfSilenceStatusPanel session={session(['admin'])} />);

    const panel = await screen.findByRole('region', { name: 'Хвилина мовчання' });
    expect(panel).toHaveTextContent('Щодня о 09:00 за київським часом (Europe/Kyiv).');
    expect(panel).toHaveTextContent('Команду поставлено в чергу.');
    expect(panel).toHaveTextContent('Це підтверджує створення команди, але не відтворення звуку');
    expect(panel).toHaveTextContent('6 листопада 2026 р. о 09:00');
  });

  it('показує причину пропуску українською', async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        ...baseStatus,
        outcome: 'skipped',
        skipReason: 'missed_window',
        processedAtUtc: '2026-11-06T07:10:00Z',
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<MinuteOfSilenceStatusPanel session={session(['auditor'])} />);

    expect(await screen.findByText('Запуск пропущено.')).toBeInTheDocument();
    expect(
      screen.getByText('Система не встигла обробити розклад у дозволене вікно запуску.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('missed_window')).not.toBeInTheDocument();
  });

  it('окремо показує відсутність запису за поточну дату', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(baseStatus));
    vi.stubGlobal('fetch', fetchMock);

    render(<MinuteOfSilenceStatusPanel session={session(['admin'])} />);

    expect(await screen.findByText('Результат ще не зафіксовано.')).toBeInTheDocument();
    expect(
      screen.getByText('Запис про обробку розкладу за цю дату поки відсутній.'),
    ).toBeInTheDocument();
  });

  it('показує помилку та дозволяє повторити запит', async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({}, 503))
      .mockResolvedValueOnce(jsonResponse({ ...baseStatus, outcome: 'queued' }));
    vi.stubGlobal('fetch', fetchMock);

    render(<MinuteOfSilenceStatusPanel session={session(['admin'])} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Не вдалося завантажити статус хвилини мовчання.',
    );
    fireEvent.click(screen.getByRole('button', { name: 'Повторити' }));
    expect(await screen.findByText('Команду поставлено в чергу.')).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('не показує локальному notificator і не робить запит', () => {
    vi.stubGlobal('fetch', fetchMock);

    render(<MinuteOfSilenceStatusPanel session={session(['notificator'])} />);

    expect(screen.queryByRole('region', { name: 'Хвилина мовчання' })).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('показує панель за capability перегляду аудиту без ролей у відповіді сесії', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(baseStatus));
    vi.stubGlobal('fetch', fetchMock);

    render(<MinuteOfSilenceStatusPanel session={sessionWithAuditCapability(true)} />);

    expect(await screen.findByRole('region', { name: 'Хвилина мовчання' })).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('не показує панель без capability перегляду аудиту навіть за наявності ролі admin', () => {
    vi.stubGlobal('fetch', fetchMock);

    render(<MinuteOfSilenceStatusPanel session={sessionWithAuditCapability(false, ['admin'])} />);

    expect(screen.queryByRole('region', { name: 'Хвилина мовчання' })).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('паузає у прихованій вкладці, поновлюється при поверненні та скасовується при unmount', async () => {
    vi.useFakeTimers();
    setDocumentHidden(true);
    const signals: AbortSignal[] = [];
    fetchMock.mockImplementation((_input, init) => {
      const signal = init?.signal;
      if (signal) signals.push(signal);
      return new Promise<Response>((_resolve, reject) => {
        signal?.addEventListener(
          'abort',
          () => reject(new DOMException('Запит скасовано', 'AbortError')),
          { once: true },
        );
      });
    });
    vi.stubGlobal('fetch', fetchMock);

    const { unmount } = render(<MinuteOfSilenceStatusPanel session={session(['auditor'])} />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(120_000);
    });
    expect(fetchMock).not.toHaveBeenCalled();

    await act(async () => {
      setDocumentHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await act(async () => {
      setDocumentHidden(true);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(signals[0]?.aborted).toBe(true);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(120_000);
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await act(async () => {
      setDocumentHidden(false);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    unmount();
    expect(signals[1]?.aborted).toBe(true);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(120_000);
      document.dispatchEvent(new Event('visibilitychange'));
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
