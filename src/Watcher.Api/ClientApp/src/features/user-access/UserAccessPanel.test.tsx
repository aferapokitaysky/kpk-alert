import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { getLocations } from '../../api';
import {
  getManagedRoleCatalog,
  getManagedUsers,
  grantManagedUserRole,
  revokeManagedUserRole,
  updateManagedUserStatus,
  type ManagedUser,
} from './api';
import { UserAccessPanel } from './UserAccessPanel';

vi.mock('../../api', () => ({
  getLocations: vi.fn(),
}));

vi.mock('./api', () => ({
  getManagedRoleCatalog: vi.fn(),
  getManagedUsers: vi.fn(),
  grantManagedUserRole: vi.fn(),
  revokeManagedUserRole: vi.fn(),
  updateManagedUserStatus: vi.fn(),
}));

const user: ManagedUser = {
  id: 'user-1',
  email: 'operator@example.test',
  isActive: true,
  roles: [
    {
      assignmentId: 'assignment-1',
      roleCode: 'notificator',
      roleName: 'Оператор',
      locationId: 'location-1',
      locationName: 'Головний корпус',
    },
  ],
};

beforeEach(() => {
  vi.mocked(getManagedUsers).mockResolvedValue({
    items: [user],
    page: 1,
    pageSize: 25,
    totalCount: 1,
  });
  vi.mocked(getManagedRoleCatalog).mockResolvedValue([
    { code: 'admin', name: 'Адміністратор', globalOnly: true, supportsLocationScope: false },
    { code: 'notificator', name: 'Оператор', globalOnly: false, supportsLocationScope: true },
  ]);
  vi.mocked(getLocations).mockResolvedValue([
    { id: 'location-1', code: 'main', name: 'Головний корпус' },
  ]);
  vi.mocked(grantManagedUserRole).mockResolvedValue(undefined);
  vi.mocked(revokeManagedUserRole).mockResolvedValue(undefined);
  vi.mocked(updateManagedUserStatus).mockResolvedValue(undefined);
  vi.stubGlobal(
    'confirm',
    vi.fn(() => true),
  );
});

describe('керування обліковими записами', () => {
  it('показує ролі користувача й додає роль до вибраного корпусу', async () => {
    const userEventInstance = userEvent.setup();
    render(<UserAccessPanel />);

    const userCard = await screen.findByRole('article', { name: 'operator@example.test' });
    expect(
      within(userCard).getByText('Головний корпус', { selector: 'small' }),
    ).toBeInTheDocument();
    await userEventInstance.selectOptions(within(userCard).getByLabelText('Роль'), 'notificator');
    await userEventInstance.selectOptions(within(userCard).getByLabelText('Доступ'), 'location-1');
    await userEventInstance.click(within(userCard).getByRole('button', { name: 'Додати роль' }));

    await waitFor(() =>
      expect(grantManagedUserRole).toHaveBeenCalledWith('user-1', 'notificator', 'location-1'),
    );
  });

  it('підтверджує деактивацію та відкликання ролі перед зміною', async () => {
    const userEventInstance = userEvent.setup();
    render(<UserAccessPanel />);
    const userCard = await screen.findByRole('article', { name: 'operator@example.test' });

    await userEventInstance.click(within(userCard).getByRole('button', { name: 'Деактивувати' }));
    await waitFor(() => expect(updateManagedUserStatus).toHaveBeenCalledWith('user-1', false));
    await userEventInstance.click(within(userCard).getByRole('button', { name: 'Відкликати' }));
    await waitFor(() =>
      expect(revokeManagedUserRole).toHaveBeenCalledWith('user-1', 'assignment-1'),
    );
  });

  it('вимагає підтвердження перед наданням глобальної ролі адміністратора', async () => {
    const userEventInstance = userEvent.setup();
    const confirmMock = vi.mocked(window.confirm);
    confirmMock.mockReturnValueOnce(false).mockReturnValueOnce(true);
    render(<UserAccessPanel />);
    const userCard = await screen.findByRole('article', { name: 'operator@example.test' });

    await userEventInstance.selectOptions(within(userCard).getByLabelText('Роль'), 'admin');
    await userEventInstance.click(within(userCard).getByRole('button', { name: 'Додати роль' }));
    expect(grantManagedUserRole).not.toHaveBeenCalled();
    expect(confirmMock).toHaveBeenCalledWith(
      'Надати глобальну роль адміністратора для operator@example.test?',
    );

    await userEventInstance.click(within(userCard).getByRole('button', { name: 'Додати роль' }));
    await waitFor(() => expect(grantManagedUserRole).toHaveBeenCalledWith('user-1', 'admin', null));
  });

  it('показує помилку сервера та дозволяє повторити завантаження', async () => {
    vi.mocked(getManagedUsers)
      .mockRejectedValueOnce(Object.assign(new Error('forbidden'), { status: 403 }))
      .mockResolvedValueOnce({ items: [user], page: 1, pageSize: 25, totalCount: 1 });
    const userEventInstance = userEvent.setup();
    render(<UserAccessPanel />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Керувати обліковими записами може лише глобальний адміністратор.',
    );
    await userEventInstance.click(screen.getByRole('button', { name: 'Повторити' }));
    expect(await screen.findByText('operator@example.test')).toBeInTheDocument();
  });
});
