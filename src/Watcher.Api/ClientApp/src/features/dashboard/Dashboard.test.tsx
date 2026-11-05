import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { SessionState } from '../../state';
import { Dashboard } from './Dashboard';

vi.mock('../alert-sources/AlertSourcesPanel', () => ({
  AlertSourcesPanel: () => null,
}));

function renderDashboard(session: SessionState) {
  return render(
    <Dashboard
      session={session}
      locations={{ status: 'empty' }}
      presets={{ status: 'empty' }}
      lastCommand={null}
      commandLocations={[]}
      onRefresh={vi.fn()}
      onRun={vi.fn()}
    />,
  );
}

describe('права на пульті', () => {
  it('пояснює режим перегляду лише за наявності доступу до аудиту', () => {
    renderDashboard({
      status: 'authenticated',
      roles: [],
      capabilities: { canOperate: false, canViewAudit: true },
    });

    expect(
      screen.getByText('Ваш обліковий запис має доступ до перегляду, але не до запуску команд.'),
    ).toHaveAttribute('role', 'status');
  });

  it('повідомляє про неналаштовані права, якщо користувач не може переглядати й запускати', () => {
    renderDashboard({
      status: 'authenticated',
      roles: [],
      capabilities: { canOperate: false, canViewAudit: false },
    });

    expect(screen.getByRole('status')).toHaveTextContent('не призначено прав у системі');
    expect(screen.queryByText(/має доступ до перегляду/)).not.toBeInTheDocument();
  });

  it('не показує повідомлення про обмеження тим, хто може створювати команди', () => {
    renderDashboard({
      status: 'authenticated',
      roles: [],
      capabilities: { canOperate: true, canViewAudit: false },
    });

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});

describe('робоча панель оператора', () => {
  it('показує статус хвилини мовчання глобальному адміністратору', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            runDate: '2026-11-06',
            timeZone: 'Europe/Kyiv',
            scheduledAtUtc: '2026-11-06T07:00:00Z',
            outcome: null,
            skipReason: null,
            commandId: null,
            processedAtUtc: null,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      ),
    );

    renderDashboard({
      status: 'authenticated',
      roles: ['admin'],
      capabilities: { canOperate: true, canViewAudit: true },
    });

    expect(await screen.findByRole('region', { name: 'Хвилина мовчання' })).toHaveTextContent(
      'Результат ще не зафіксовано.',
    );
  });

  it('показує вибір корпусу перед сценаріями та передає вибір до підтвердження', async () => {
    const user = userEvent.setup();
    const onRun = vi.fn();
    const location = { id: 'main', name: 'Головний корпус', code: 'main' };
    const preset = { code: 'evacuation', name: 'Евакуація' };
    const { container } = render(
      <Dashboard
        session={{
          status: 'authenticated',
          roles: ['notificator'],
          capabilities: { canOperate: true, canViewAudit: false },
        }}
        locations={{ status: 'ready', data: [location] }}
        presets={{ status: 'ready', data: [preset] }}
        lastCommand={null}
        commandLocations={[]}
        onRefresh={vi.fn()}
        onRun={onRun}
      />,
    );

    const workspace = container.querySelector('.dispatch-grid');
    expect(workspace).not.toBeNull();
    expect(screen.queryByRole('region', { name: 'Хвилина мовчання' })).not.toBeInTheDocument();
    expect(
      within(workspace as HTMLElement)
        .getAllByRole('heading', { level: 2 })
        .map((heading) => heading.textContent),
    ).toEqual(['Корпуси', 'Сценарії']);

    await user.click(screen.getByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: /Евакуація/ }));

    expect(onRun).toHaveBeenCalledWith(preset, ['main']);
  });
});
