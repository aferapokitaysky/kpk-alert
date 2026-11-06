import { useCallback, useEffect, useRef, useState } from 'react';
import { getLocations, type Location } from '../../api';
import {
  getManagedRoleCatalog,
  getManagedUsers,
  grantManagedUserRole,
  revokeManagedUserRole,
  updateManagedUserStatus,
  type ManagedRoleCatalogEntry,
  type ManagedUser,
} from './api';

type PanelState =
  | { status: 'loading' }
  | {
      status: 'ready';
      users: ManagedUser[];
      totalCount: number;
      roles: ManagedRoleCatalogEntry[];
      locations: Location[];
    }
  | { status: 'error' };

function loadErrorMessage(error: unknown): string {
  if (error instanceof Error && 'status' in error) {
    const status = (error as Error & { status?: number }).status;
    if (status === 401) return 'Сесію завершено. Увійдіть знову.';
    if (status === 403) return 'Керувати обліковими записами може лише глобальний адміністратор.';
  }
  return 'Не вдалося завантажити облікові записи. Перевірте з’єднання та повторіть спробу.';
}

function mutationErrorMessage(error: unknown): string {
  if (error instanceof Error && 'status' in error) {
    const status = (error as Error & { status?: number }).status;
    if (status === 401) return 'Сесію завершено. Увійдіть знову.';
    if (status === 403) return 'Ваші права змінилися. Оновіть дані.';
    if (status === 404) return 'Обліковий запис або призначення вже видалене. Оновіть список.';
    if (status === 409) return 'Зміна порушує правило доступу. Перевірте активних адміністраторів.';
    if (status === 422) return 'Роль або корпус більше недоступні. Оновіть список.';
  }
  return 'Не вдалося зберегти зміну. Перевірте з’єднання та повторіть спробу.';
}

function UserAccessCard({
  user,
  roles,
  locations,
  pending,
  onStatusChange,
  onGrantRole,
  onRevokeRole,
}: {
  user: ManagedUser;
  roles: ManagedRoleCatalogEntry[];
  locations: Location[];
  pending: boolean;
  onStatusChange: (user: ManagedUser, isActive: boolean) => void;
  onGrantRole: (userId: string, roleCode: string, locationId: string | null) => void;
  onRevokeRole: (userId: string, assignmentId: string) => void;
}) {
  const [selectedRoleCode, setSelectedRoleCode] = useState('');
  const [selectedLocationId, setSelectedLocationId] = useState('');
  const selectedRole = roles.find((role) => role.code === selectedRoleCode);

  return (
    <article className="user-access-card" aria-labelledby={`user-${user.id}`}>
      <div className="user-access-heading">
        <div>
          <h2 id={`user-${user.id}`}>{user.email}</h2>
          <span className={`user-access-status${user.isActive ? ' is-active' : ' is-inactive'}`}>
            {user.isActive ? 'Активний' : 'Деактивований'}
          </span>
        </div>
        <button
          className="button button-secondary user-status-button"
          type="button"
          disabled={pending}
          onClick={() => onStatusChange(user, !user.isActive)}
        >
          {user.isActive ? 'Деактивувати' : 'Активувати'}
        </button>
      </div>

      <section className="user-role-list" aria-label={`Ролі: ${user.email}`}>
        <h3>Призначені ролі</h3>
        {user.roles.length === 0 ? (
          <p className="user-access-muted">Ролей поки немає.</p>
        ) : (
          <ul>
            {user.roles.map((role) => (
              <li key={role.assignmentId}>
                <span>
                  <strong>{role.roleName}</strong>
                  <small>{role.locationName ?? 'Увесь заклад'}</small>
                </span>
                <button
                  className="inline-button"
                  type="button"
                  disabled={pending}
                  onClick={() => onRevokeRole(user.id, role.assignmentId)}
                >
                  Відкликати
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <form
        className="user-role-form"
        onSubmit={(event) => {
          event.preventDefault();
          if (!selectedRole) return;
          if (
            selectedRole.code === 'admin' &&
            !window.confirm(`Надати глобальну роль адміністратора для ${user.email}?`)
          ) {
            return;
          }
          onGrantRole(
            user.id,
            selectedRole.code,
            selectedRole.globalOnly || !selectedLocationId ? null : selectedLocationId,
          );
        }}
      >
        <label>
          <span>Роль</span>
          <select
            value={selectedRoleCode}
            onChange={(event) => setSelectedRoleCode(event.target.value)}
            disabled={pending || roles.length === 0}
            required
          >
            <option value="">Оберіть роль</option>
            {roles.map((role) => (
              <option key={role.code} value={role.code}>
                {role.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span>Доступ</span>
          <select
            value={selectedRole?.globalOnly ? '' : selectedLocationId}
            onChange={(event) => setSelectedLocationId(event.target.value)}
            disabled={pending || !selectedRole?.supportsLocationScope || selectedRole.globalOnly}
          >
            <option value="">Увесь заклад</option>
            {locations.map((location) => (
              <option key={location.id} value={location.id}>
                {location.name}
              </option>
            ))}
          </select>
        </label>
        <button className="button button-primary" type="submit" disabled={pending || !selectedRole}>
          Додати роль
        </button>
      </form>
    </article>
  );
}

export function UserAccessPanel() {
  const [page, setPage] = useState(1);
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [state, setState] = useState<PanelState>({ status: 'loading' });
  const [pendingUserId, setPendingUserId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const activeRequest = useRef<AbortController | null>(null);
  const actionLock = useRef(false);

  const load = useCallback(async () => {
    activeRequest.current?.abort();
    const controller = new AbortController();
    activeRequest.current = controller;
    try {
      const [users, roles, locations] = await Promise.all([
        getManagedUsers(page, search, controller.signal),
        getManagedRoleCatalog(controller.signal),
        getLocations(),
      ]);
      if (!controller.signal.aborted) {
        setState({
          status: 'ready',
          users: users.items,
          totalCount: users.totalCount,
          roles,
          locations,
        });
        setError(null);
      }
    } catch (loadError) {
      if (!controller.signal.aborted) {
        setState({ status: 'error' });
        setError(loadErrorMessage(loadError));
      }
    } finally {
      if (activeRequest.current === controller) activeRequest.current = null;
    }
  }, [page, search]);

  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 0);
    return () => {
      window.clearTimeout(timer);
      activeRequest.current?.abort();
    };
  }, [load]);

  const runMutation = async (userId: string, action: () => Promise<void>) => {
    if (actionLock.current) return;
    actionLock.current = true;
    setPendingUserId(userId);
    setError(null);
    try {
      await action();
      await load();
    } catch (mutationError) {
      setError(mutationErrorMessage(mutationError));
    } finally {
      actionLock.current = false;
      setPendingUserId(null);
    }
  };

  return (
    <section className="user-access-page" aria-labelledby="user-access-title">
      <header className="page-heading">
        <div>
          <p className="eyebrow">АДМІНІСТРУВАННЯ</p>
          <h1 id="user-access-title">Облікові записи</h1>
          <p className="lead">
            Керуйте доступом уже зареєстрованих користувачів. Нових користувачів додає
            відповідальний адміністратор.
          </p>
        </div>
      </header>

      <form
        className="user-search"
        role="search"
        onSubmit={(event) => {
          event.preventDefault();
          setPage(1);
          setSearch(searchInput.trim());
        }}
      >
        <label htmlFor="user-search-input">Пошук за email</label>
        <input
          id="user-search-input"
          type="search"
          maxLength={100}
          value={searchInput}
          onChange={(event) => setSearchInput(event.target.value)}
        />
        <button className="button button-secondary" type="submit">
          Знайти
        </button>
      </form>

      {error && (
        <div className="alert alert-error" role="alert">
          <span>{error}</span>
          <button className="inline-button" type="button" onClick={() => void load()}>
            Повторити
          </button>
        </div>
      )}

      {state.status === 'loading' && (
        <div className="state-card" role="status">
          Завантажуємо облікові записи…
        </div>
      )}
      {state.status === 'ready' && state.users.length === 0 && (
        <div className="state-card">За цим запитом користувачів не знайдено.</div>
      )}
      {state.status === 'ready' && state.users.length > 0 && (
        <>
          <p className="user-result-count">Облікових записів: {state.totalCount}</p>
          <div className="user-access-list">
            {state.users.map((user) => (
              <UserAccessCard
                key={user.id}
                user={user}
                roles={state.roles}
                locations={state.locations}
                pending={pendingUserId === user.id}
                onStatusChange={(target, isActive) => {
                  const action = isActive ? 'активувати' : 'деактивувати';
                  if (window.confirm(`Підтвердити дію «${action}» для ${target.email}?`)) {
                    void runMutation(target.id, () => updateManagedUserStatus(target.id, isActive));
                  }
                }}
                onGrantRole={(targetId, roleCode, locationId) =>
                  void runMutation(targetId, () =>
                    grantManagedUserRole(targetId, roleCode, locationId),
                  )
                }
                onRevokeRole={(targetId, assignmentId) => {
                  const role = user.roles.find((item) => item.assignmentId === assignmentId);
                  if (
                    role &&
                    window.confirm(`Відкликати роль «${role.roleName}» для ${user.email}?`)
                  ) {
                    void runMutation(targetId, () => revokeManagedUserRole(targetId, assignmentId));
                  }
                }}
              />
            ))}
          </div>
          <nav className="user-pagination" aria-label="Сторінки користувачів">
            <button
              className="button button-secondary"
              type="button"
              disabled={page <= 1 || pendingUserId !== null}
              onClick={() => setPage((current) => Math.max(1, current - 1))}
            >
              Назад
            </button>
            <span>Сторінка {page}</span>
            <button
              className="button button-secondary"
              type="button"
              disabled={page * 25 >= state.totalCount || pendingUserId !== null}
              onClick={() => setPage((current) => current + 1)}
            >
              Далі
            </button>
          </nav>
        </>
      )}
    </section>
  );
}
