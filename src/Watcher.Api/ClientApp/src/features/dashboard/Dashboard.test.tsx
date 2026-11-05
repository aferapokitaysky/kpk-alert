import { render, screen } from '@testing-library/react';
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

    expect(screen.getByRole('status')).toHaveTextContent(
      'має доступ до перегляду, але не до запуску команд',
    );
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
