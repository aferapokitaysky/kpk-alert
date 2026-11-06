import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MinuteOfSilenceHistoryPanel } from './MinuteOfSilenceHistoryPanel';
import { getMinuteOfSilenceHistory } from './history-api';

vi.mock('./history-api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./history-api')>();
  return { ...actual, getMinuteOfSilenceHistory: vi.fn() };
});

const getHistoryMock = vi.mocked(getMinuteOfSilenceHistory);
const auditorSession = {
  status: 'authenticated' as const,
  roles: ['auditor'],
  capabilities: { canOperate: false, canViewAudit: true },
};

afterEach(() => {
  getHistoryMock.mockReset();
});

describe('історія хвилини мовчання', () => {
  it('показує лише збережений результат і час за київським поясом', async () => {
    getHistoryMock.mockResolvedValue({
      items: [
        {
          runDate: '2026-11-06',
          outcome: 'skipped',
          skipReason: 'missed_window',
          processedAtUtc: '2026-11-06T07:02:00Z',
        },
      ],
      from: '2026-10-08',
      to: '2026-11-06',
      page: 1,
      pageSize: 25,
      totalCount: 1,
    });

    render(
      <MinuteOfSilenceHistoryPanel
        session={auditorSession}
        onSessionExpired={vi.fn()}
        onSessionRetry={vi.fn()}
      />,
    );

    expect(await screen.findByText('Запуск пропущено')).toBeInTheDocument();
    expect(screen.getByText('Запуск не відбувся у дозволене часове вікно.')).toBeInTheDocument();
    expect(screen.getByText(/Оброблено 6 лист. 2026 р., 09:02/)).toBeInTheDocument();
    expect(getHistoryMock).toHaveBeenCalledWith(
      expect.any(String),
      expect.any(String),
      1,
      25,
      expect.any(AbortSignal),
    );
  });

  it('переходить між сторінками результатів', async () => {
    getHistoryMock
      .mockResolvedValueOnce({
        items: [
          {
            runDate: '2026-11-06',
            outcome: 'queued',
            skipReason: null,
            processedAtUtc: '2026-11-06T07:00:00Z',
          },
        ],
        from: '2026-10-08',
        to: '2026-11-06',
        page: 1,
        pageSize: 25,
        totalCount: 26,
      })
      .mockResolvedValueOnce({
        items: [
          {
            runDate: '2026-11-05',
            outcome: 'skipped',
            skipReason: 'outside_execution_window',
            processedAtUtc: '2026-11-05T07:04:00Z',
          },
        ],
        from: '2026-10-08',
        to: '2026-11-06',
        page: 2,
        pageSize: 25,
        totalCount: 26,
      });

    render(
      <MinuteOfSilenceHistoryPanel
        session={auditorSession}
        onSessionExpired={vi.fn()}
        onSessionRetry={vi.fn()}
      />,
    );

    expect(await screen.findByText('Команду поставлено в чергу')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Далі' }));
    expect(await screen.findByText('Запуск пропущено')).toBeInTheDocument();
    expect(getHistoryMock).toHaveBeenLastCalledWith(
      expect.any(String),
      expect.any(String),
      2,
      25,
      expect.any(AbortSignal),
    );
  });

  it('повертається на останню сторінку після скорочення історії', async () => {
    const queuedItem = {
      runDate: '2026-11-06',
      outcome: 'queued' as const,
      skipReason: null,
      processedAtUtc: '2026-11-06T07:00:00Z',
    };
    getHistoryMock
      .mockResolvedValueOnce({
        items: [queuedItem],
        from: '2026-10-08',
        to: '2026-11-06',
        page: 1,
        pageSize: 25,
        totalCount: 26,
      })
      .mockResolvedValueOnce({
        items: [],
        from: '2026-10-08',
        to: '2026-11-06',
        page: 2,
        pageSize: 25,
        totalCount: 1,
      })
      .mockResolvedValueOnce({
        items: [queuedItem],
        from: '2026-10-08',
        to: '2026-11-06',
        page: 1,
        pageSize: 25,
        totalCount: 1,
      });

    render(
      <MinuteOfSilenceHistoryPanel
        session={auditorSession}
        onSessionExpired={vi.fn()}
        onSessionRetry={vi.fn()}
      />,
    );

    expect(await screen.findByText('Команду поставлено в чергу')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Далі' }));
    await waitFor(() => expect(getHistoryMock).toHaveBeenCalledTimes(3));
    expect(await screen.findByText(/Збережені результати запуску о/)).toBeInTheDocument();
    expect(getHistoryMock).toHaveBeenLastCalledWith(
      expect.any(String),
      expect.any(String),
      1,
      25,
      expect.any(AbortSignal),
    );
    expect(screen.queryByText(/На цій сторінці результатів немає/)).not.toBeInTheDocument();
  });

  it('залишає доступною пагінацію для порожньої сторінки з ненульовим лічильником', async () => {
    getHistoryMock
      .mockResolvedValueOnce({
        items: [
          {
            runDate: '2026-11-06',
            outcome: 'queued',
            skipReason: null,
            processedAtUtc: '2026-11-06T07:00:00Z',
          },
        ],
        from: '2026-10-08',
        to: '2026-11-06',
        page: 1,
        pageSize: 25,
        totalCount: 26,
      })
      .mockResolvedValueOnce({
        items: [],
        from: '2026-10-08',
        to: '2026-11-06',
        page: 2,
        pageSize: 25,
        totalCount: 26,
      });

    render(
      <MinuteOfSilenceHistoryPanel
        session={auditorSession}
        onSessionExpired={vi.fn()}
        onSessionRetry={vi.fn()}
      />,
    );

    expect(await screen.findByText('Команду поставлено в чергу')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Далі' }));
    expect(await screen.findByText(/На цій сторінці результатів немає/)).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Сторінки історії' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Назад' })).toBeEnabled();
    expect(screen.getByText('Сторінка 2 з 2')).toBeInTheDocument();
  });

  it('дозволяє очистити будь-яку дату без помилки під час рендерингу', async () => {
    getHistoryMock.mockResolvedValue({
      items: [],
      from: '2026-10-08',
      to: '2026-11-06',
      page: 1,
      pageSize: 25,
      totalCount: 0,
    });

    render(
      <MinuteOfSilenceHistoryPanel
        session={auditorSession}
        onSessionExpired={vi.fn()}
        onSessionRetry={vi.fn()}
      />,
    );

    await screen.findByText('За цей період збережених результатів немає.');
    fireEvent.change(screen.getByLabelText('Від дати'), { target: { value: '' } });
    expect(screen.getByLabelText('Від дати')).toHaveValue('');

    fireEvent.change(screen.getByLabelText('До дати'), { target: { value: '' } });
    expect(screen.getByLabelText('До дати')).toHaveValue('');
    expect(screen.getByRole('heading', { name: 'Історія результатів' })).toBeInTheDocument();
  });

  it('не запитує історію без ролі аудитора або адміністратора', () => {
    render(
      <MinuteOfSilenceHistoryPanel
        session={{
          status: 'authenticated',
          roles: ['notificator'],
          capabilities: { canOperate: true, canViewAudit: false },
        }}
        onSessionExpired={vi.fn()}
        onSessionRetry={vi.fn()}
      />,
    );

    expect(screen.queryByRole('region', { name: 'Історія результатів' })).not.toBeInTheDocument();
    expect(getHistoryMock).not.toHaveBeenCalled();
  });
});
