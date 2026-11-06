import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { SessionState } from '../../state';
import { DeviceConnectionPanel } from './DeviceConnectionPanel';

const response = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function session(canOperate: boolean): SessionState {
  return {
    status: 'authenticated',
    roles: canOperate ? ['notificator'] : [],
    capabilities: { canOperate, canViewAudit: false },
  };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('панель підключення вузлів', () => {
  it('показує підключені й відключені вузли, не називаючи це перевіркою аудіо', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        response([
          {
            deviceId: 'device-1',
            deviceName: 'Перший вузол',
            locationId: 'location-1',
            locationName: 'Головний корпус',
            connectionState: 'connected',
          },
          {
            deviceId: 'device-2',
            deviceName: 'Другий вузол',
            locationId: 'location-1',
            locationName: 'Головний корпус',
            connectionState: 'offline',
          },
        ]),
      ),
    );

    render(<DeviceConnectionPanel session={session(true)} />);

    expect(await screen.findByText('1 з 2 на зв’язку')).toBeInTheDocument();
    expect(screen.getByText('Підключений')).toBeInTheDocument();
    expect(screen.getByText('Не підключений')).toBeInTheDocument();
    expect(screen.getByText(/не підтверджує стан аудіо/)).toBeInTheDocument();
  });

  it('повідомляє про помилку завантаження', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(response({}, 503)));

    render(<DeviceConnectionPanel session={session(true)} />);

    await waitFor(() =>
      expect(screen.getByText(/Не вдалося завантажити статус підключень/)).toBeInTheDocument(),
    );
  });

  it('не показує панель користувачу без дозволу на роботу', () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    render(<DeviceConnectionPanel session={session(false)} />);
    expect(screen.queryByRole('region')).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
