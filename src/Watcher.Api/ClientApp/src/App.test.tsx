import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from './App';
import {
  ApiError,
  createAlertCommand,
  getAuditEvents,
  getLocations,
  getPresets,
  getReadiness,
  getSession,
  logout,
} from './api';

vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return {
    ...actual,
    createAlertCommand: vi.fn(),
    getAuditEvents: vi.fn(),
    getLocations: vi.fn(),
    getPresets: vi.fn(),
    getReadiness: vi.fn(),
    getSession: vi.fn(),
    logout: vi.fn(),
  };
});

vi.mock('./features/alert-sources/AlertSourcesPanel', () => ({
  AlertSourcesPanel: () => null,
}));

const location = { id: 'location-1', name: 'Головний корпус', code: 'main' };
const preset = { code: 'evacuation', name: 'Евакуація' };
const command = {
  id: 'command-1',
  action: 'start' as const,
  presetCode: preset.code,
  createdAtUtc: '2026-11-05T12:00:00Z',
  targets: [{ locationId: location.id, state: 'Created' }],
};

function setViewport(isMobile: boolean) {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: isMobile && query === '(max-width: 820px)',
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

beforeEach(() => {
  window.history.replaceState(null, '', '/');
  setViewport(false);
  vi.mocked(getSession).mockResolvedValue({
    authenticated: true,
    roles: ['admin'],
    capabilities: { canOperate: true, canViewAudit: true },
  });
  vi.mocked(getLocations).mockResolvedValue([location]);
  vi.mocked(getPresets).mockResolvedValue([preset]);
  vi.mocked(getReadiness).mockResolvedValue({ httpStatus: 200, status: 'ok' });
  vi.mocked(getAuditEvents).mockResolvedValue({ items: [], page: 1, pageSize: 25, totalCount: 0 });
  vi.mocked(createAlertCommand).mockResolvedValue(command);
  vi.mocked(logout).mockResolvedValue(undefined);
});

describe('пульт оператора', () => {
  it('показує завантаження й окрему помилку списку корпусів', async () => {
    vi.mocked(getLocations).mockRejectedValue(new ApiError(503));
    render(<App />);
    expect(screen.getByText('Завантажуємо доступні корпуси…')).toBeInTheDocument();
    expect(await screen.findByText('Не вдалося завантажити корпуси.')).toBeInTheDocument();
  });

  it('розрізняє відсутню сесію й не показує кнопку виходу', async () => {
    vi.mocked(getSession).mockRejectedValue(new ApiError(401));
    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Увійдіть до системи' })).toBeInTheDocument();
    expect(screen.getAllByRole('link', { name: 'Увійти через Google' }).length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: 'Вийти' })).not.toBeInTheDocument();
    expect(getLocations).not.toHaveBeenCalled();
  });

  it('пояснює, коли сервер відхиляє обліковий запис', async () => {
    vi.mocked(getSession).mockRejectedValue(new ApiError(403));
    render(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Обліковий запис не має доступу до системи.',
    );
    expect(screen.queryByRole('button', { name: 'Вийти' })).not.toBeInTheDocument();
    expect(getLocations).not.toHaveBeenCalled();
  });

  it('пояснює відсутність обох можливостей у сесії', async () => {
    vi.mocked(getSession).mockResolvedValue({
      authenticated: true,
      roles: [],
      capabilities: { canOperate: false, canViewAudit: false },
    });
    render(<App />);

    expect(
      await screen.findByText(/Для вашого облікового запису не призначено прав у системі\./),
    ).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Журнал дій' })).not.toBeInTheDocument();
  });

  it('завершує авторизовану сесію через API та оновлює інтерфейс', async () => {
    const user = userEvent.setup();
    render(<App />);

    await user.click(await screen.findByRole('button', { name: 'Вийти' }));
    expect(await screen.findByRole('heading', { name: 'Увійдіть до системи' })).toBeInTheDocument();
    expect(logout).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Вийти' })).not.toBeInTheDocument();
  });

  it('підтверджує запуск, показує відмову в діалозі й зберігає ключ повтору', async () => {
    const user = userEvent.setup();
    vi.mocked(createAlertCommand)
      .mockRejectedValueOnce(new ApiError(409))
      .mockResolvedValueOnce(command);
    render(<App />);

    await user.click(await screen.findByRole('checkbox', { name: /Головний корпус/ }));
    await user.click(screen.getByRole('button', { name: /Евакуація/ }));
    const dialog = screen.getByRole('dialog', { name: 'Запустити сценарій?' });
    await user.click(screen.getByRole('button', { name: 'Підтвердити запуск' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('ключ');
    expect(dialog).toHaveAttribute('open');

    await user.click(screen.getByRole('button', { name: 'Підтвердити запуск' }));
    expect(
      await screen.findByRole('heading', { name: 'Команду прийнято до черги' }),
    ).toBeInTheDocument();
    expect(createAlertCommand).toHaveBeenCalledTimes(2);
    expect(createAlertCommand).toHaveBeenNthCalledWith(
      1,
      'evacuation',
      ['location-1'],
      expect.any(String),
    );
    expect(vi.mocked(createAlertCommand).mock.calls[1]?.[2]).toBe(
      vi.mocked(createAlertCommand).mock.calls[0]?.[2],
    );
    expect(screen.getByText(/не є підтвердженням відтворення звуку/i)).toBeInTheDocument();
  });

  it('керує мобільним меню клавіатурою та прибирає закриту панель із фокусу', async () => {
    const user = userEvent.setup();
    setViewport(true);
    render(<App />);

    const sidebar = document.querySelector<HTMLElement>('.sidebar');
    const main = document.querySelector<HTMLElement>('.main-content');
    const toggle = await screen.findByRole('button', { name: 'Відкрити меню' });
    expect(sidebar).toHaveAttribute('inert');
    expect(sidebar).toHaveAttribute('aria-hidden', 'true');
    expect(main).not.toHaveAttribute('inert');

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(sidebar).not.toHaveAttribute('inert');
    expect(main).toHaveAttribute('inert');
    expect(sidebar?.contains(document.activeElement)).toBe(true);

    await user.keyboard('{Escape}');
    await waitFor(() => expect(sidebar).toHaveAttribute('inert'));
    expect(toggle).toHaveFocus();
  });

  it('завантажує фільтрований журнал і переходить між сторінками', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', '/#audit');
    vi.mocked(getAuditEvents).mockResolvedValue({
      items: [
        {
          id: 'event-1',
          eventType: 'auth.logout',
          actorType: 'user',
          actorId: 'user-1',
          occurredAtUtc: '2026-11-05T12:00:00Z',
          payload: { method: 'session' },
        },
      ],
      page: 1,
      pageSize: 25,
      totalCount: 26,
    });
    render(<App />);

    expect(await screen.findByText('auth.logout')).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'На сторінці' })).toHaveClass('form-control-sm');
    await user.type(screen.getByRole('textbox', { name: 'Тип події' }), 'auth.logout');
    await user.click(screen.getByRole('button', { name: 'Застосувати фільтр' }));
    await waitFor(() => expect(getAuditEvents).toHaveBeenLastCalledWith(1, 25, 'auth.logout'));
    await user.click(screen.getByRole('button', { name: 'Наступна' }));
    await waitFor(() => expect(getAuditEvents).toHaveBeenLastCalledWith(2, 25, 'auth.logout'));
  });

  it('зберігає компактний select журнала шириною Academica та SVG-стрілкою', () => {
    const styles = readFileSync(
      resolve(process.cwd(), 'src/Watcher.Api/ClientApp/src/styles/app.css'),
      'utf8',
    );
    const compactSelectStyles = styles.match(/\.form-control-sm\s*\{([^}]*)\}/s)?.[1] ?? '';

    expect(compactSelectStyles).toContain('min-width: 132px;');
    expect(compactSelectStyles).toContain('appearance: none;');
    expect(compactSelectStyles).toContain('background-image: url("data:image/svg+xml,');
    expect(compactSelectStyles).toContain('m6 9 6 6 6-6');
  });

  it('обмежує розділи за можливостями сесії, а не за локальними ролями', async () => {
    window.history.replaceState(null, '', '/#audit');
    vi.mocked(getSession).mockResolvedValue({
      authenticated: true,
      roles: [],
      capabilities: { canOperate: false, canViewAudit: true },
    });
    render(<App />);

    expect(await screen.findByRole('heading', { name: 'Журнал дій' })).toBeInTheDocument();
    await waitFor(() => expect(getAuditEvents).toHaveBeenCalledWith(1, 25, ''));
    expect(screen.getByRole('link', { name: 'Журнал дій' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Пульт керування' })).not.toBeInTheDocument();
  });

  it('показує заборону доступу до журналу, якщо сервер повернув 403', async () => {
    window.history.replaceState(null, '', '/#audit');
    vi.mocked(getAuditEvents).mockRejectedValue(new ApiError(403));
    render(<App />);

    expect(await screen.findByRole('alert')).toHaveTextContent('лише аудиторам і адміністраторам');
  });
});
