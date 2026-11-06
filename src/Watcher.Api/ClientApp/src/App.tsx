import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  createAlertCommand,
  createStopCommand,
  getAlertCommand,
  getLocations,
  getPresets,
  getReadiness,
  getSession,
  logout,
  type AlertCommand,
  type Location,
  type Preset,
} from './api';
import { AuditPage } from './features/audit/AuditPage';
import { GoogleLoginLink } from './features/auth/GoogleLoginLink';
import { Dashboard } from './features/dashboard/Dashboard';
import { CommandDialog } from './features/dashboard/CommandDialog';
import { StopCommandDialog } from './features/dashboard/StopCommandDialog';
import { UserAccessPanel } from './features/user-access/UserAccessPanel';
import { SoundPresetsPage } from './features/sound-presets/SoundPresetsPage';
import {
  clearLastCommandId,
  readLastCommandId,
  saveLastCommandId,
} from './features/dashboard/last-command-storage';
import {
  commandErrorMessage,
  readinessFromResponse,
  resourceFromResult,
  stopCommandErrorMessage,
  type ReadinessState,
  type ResourceState,
  type SessionState,
} from './state';

type Page = 'dashboard' | 'audit' | 'users' | 'presets';

function pageFromHash(): Page {
  if (window.location.hash === '#audit') return 'audit';
  if (window.location.hash === '#users') return 'users';
  if (window.location.hash === '#presets') return 'presets';
  return 'dashboard';
}

function useMobileViewport(onDesktop: () => void): boolean {
  const [isMobile, setIsMobile] = useState(() => window.matchMedia('(max-width: 820px)').matches);
  useEffect(() => {
    const query = window.matchMedia('(max-width: 820px)');
    const update = () => {
      setIsMobile(query.matches);
      if (!query.matches) onDesktop();
    };
    query.addEventListener('change', update);
    return () => query.removeEventListener('change', update);
  }, [onDesktop]);
  return isMobile;
}

function readinessLabel(state: ReadinessState): string {
  if (state === 'available') return 'Сервер готовий';
  if (state === 'unavailable') return 'Сервер недоступний';
  return 'Стан сервера невідомий';
}

function App() {
  const [page, setPage] = useState<Page>(pageFromHash);
  const [session, setSession] = useState<SessionState>({ status: 'loading' });
  const [locations, setLocations] = useState<ResourceState<Location[]>>({ status: 'loading' });
  const [presets, setPresets] = useState<ResourceState<Preset[]>>({ status: 'loading' });
  const [readiness, setReadiness] = useState<ReadinessState>('unknown');
  const [offlineShell, setOfflineShell] = useState<'checking' | 'ready' | 'unavailable'>(() =>
    import.meta.env.DEV || !('serviceWorker' in navigator) ? 'unavailable' : 'checking',
  );
  const [menuOpen, setMenuOpen] = useState(false);
  const [logoutPending, setLogoutPending] = useState(false);
  const [headerError, setHeaderError] = useState('');
  const [pendingCommand, setPendingCommand] = useState<{
    preset: Preset;
    locationIds: string[];
  } | null>(null);
  const [pendingStop, setPendingStop] = useState<{
    commandId: string;
    locations: Location[];
  } | null>(null);
  const [commandPending, setCommandPending] = useState(false);
  const [commandError, setCommandError] = useState('');
  const [lastCommand, setLastCommand] = useState<AlertCommand | null>(null);
  const [lastCommandLocations, setLastCommandLocations] = useState<Location[]>([]);
  const [lastCommandRestoreStatus, setLastCommandRestoreStatus] = useState<
    'idle' | 'loading' | 'error'
  >('idle');
  const retryKeyRef = useRef<{ fingerprint: string; key: string } | null>(null);
  const menuToggleRef = useRef<HTMLButtonElement>(null);
  const sidebarRef = useRef<HTMLElement>(null);
  const mainRef = useRef<HTMLElement>(null);
  const closeMenu = useCallback(() => setMenuOpen(false), []);
  const isMobile = useMobileViewport(closeMenu);

  const refreshData = useCallback(async () => {
    setHeaderError('');
    setSession({ status: 'loading' });
    setLocations({ status: 'loading' });
    setPresets({ status: 'loading' });
    try {
      const operatorSession = await getSession();
      setSession({
        status: 'authenticated',
        roles: operatorSession.roles,
        capabilities: operatorSession.capabilities,
      });
      const [locationsResult, presetsResult] = await Promise.allSettled([
        getLocations(),
        getPresets(),
      ]);
      setLocations(resourceFromResult(locationsResult, (items) => items.length === 0));
      setPresets(resourceFromResult(presetsResult, (items) => items.length === 0));

      const savedCommandId = readLastCommandId();
      if (!savedCommandId) {
        setLastCommand(null);
        setLastCommandLocations([]);
        setLastCommandRestoreStatus('idle');
        return;
      }
      if (!operatorSession.capabilities.canOperate) {
        clearLastCommandId();
        setLastCommand(null);
        setLastCommandLocations([]);
        setLastCommandRestoreStatus('idle');
        return;
      }

      setLastCommandRestoreStatus('loading');
      try {
        const restoredCommand = await getAlertCommand(savedCommandId);
        const availableLocations =
          locationsResult.status === 'fulfilled' ? locationsResult.value : [];
        setLastCommand(restoredCommand);
        setLastCommandLocations(
          restoredCommand.targets.flatMap((target) => {
            const location = availableLocations.find((item) => item.id === target.locationId);
            return location ? [location] : [];
          }),
        );
        setLastCommandRestoreStatus('idle');
      } catch (error) {
        if (error instanceof ApiError && [401, 403, 404].includes(error.status)) {
          if (error.status !== 401) clearLastCommandId();
          setLastCommand(null);
          setLastCommandLocations([]);
          setLastCommandRestoreStatus('idle');
          if (error.status === 401) {
            setSession({ status: 'unauthenticated' });
            setLocations({ status: 'unauthenticated' });
            setPresets({ status: 'unauthenticated' });
          }
        } else {
          setLastCommandRestoreStatus('error');
        }
      }
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        setSession({ status: 'unauthenticated' });
        setLocations({ status: 'unauthenticated' });
        setPresets({ status: 'unauthenticated' });
        setLastCommand(null);
        setLastCommandLocations([]);
        setLastCommandRestoreStatus('idle');
      } else if (error instanceof ApiError && error.status === 403) {
        setSession({ status: 'forbidden' });
        setLocations({ status: 'forbidden' });
        setPresets({ status: 'forbidden' });
        setLastCommand(null);
        setLastCommandLocations([]);
        setLastCommandRestoreStatus('idle');
      } else {
        setSession({ status: 'error' });
        setLocations({ status: 'error' });
        setPresets({ status: 'error' });
        setLastCommand(null);
        setLastCommandLocations([]);
        setLastCommandRestoreStatus('idle');
      }
    }
  }, []);

  const refreshReadiness = useCallback(async () => {
    try {
      const response = await getReadiness();
      setReadiness(readinessFromResponse(response.httpStatus, response.status));
    } catch {
      setReadiness('unknown');
    }
  }, []);

  useEffect(() => {
    const syncPage = () => setPage(pageFromHash());
    window.addEventListener('hashchange', syncPage);
    const refreshTimer = window.setTimeout(() => void refreshData(), 0);
    const readinessTimer = window.setTimeout(() => void refreshReadiness(), 0);
    const readinessInterval = window.setInterval(() => void refreshReadiness(), 30_000);
    return () => {
      window.removeEventListener('hashchange', syncPage);
      window.clearTimeout(refreshTimer);
      window.clearTimeout(readinessTimer);
      window.clearInterval(readinessInterval);
    };
  }, [refreshData, refreshReadiness]);

  useEffect(() => {
    if (import.meta.env.DEV || !('serviceWorker' in navigator)) return;
    void navigator.serviceWorker
      .register('/service-worker.js')
      .then(() => setOfflineShell('ready'))
      .catch(() => setOfflineShell('unavailable'));
  }, []);

  useEffect(() => {
    const closedOnMobile = isMobile && !menuOpen;
    if (sidebarRef.current) {
      sidebarRef.current.toggleAttribute('inert', closedOnMobile);
      if (isMobile) sidebarRef.current.setAttribute('aria-hidden', String(closedOnMobile));
      else sidebarRef.current.removeAttribute('aria-hidden');
    }
    if (mainRef.current) {
      mainRef.current.toggleAttribute('inert', isMobile && menuOpen);
      if (isMobile && menuOpen) mainRef.current.setAttribute('aria-hidden', 'true');
      else mainRef.current.removeAttribute('aria-hidden');
    }
    document.body.classList.toggle('menu-open', isMobile && menuOpen);
    if (menuOpen && isMobile)
      sidebarRef.current?.querySelector<HTMLAnchorElement>('.nav-link')?.focus();
  }, [isMobile, menuOpen]);

  useEffect(() => {
    if (session.status !== 'authenticated') return;
    const { canOperate, canViewAudit } = session.capabilities;
    if ((page === 'users' || page === 'presets') && !session.roles.includes('admin')) {
      window.location.hash = canViewAudit ? 'audit' : 'dashboard';
      return;
    }
    if (!canOperate && canViewAudit && page === 'dashboard') window.location.hash = 'audit';
  }, [page, session]);

  useEffect(() => {
    if (menuOpen && isMobile) return;
    if (sidebarRef.current?.contains(document.activeElement)) menuToggleRef.current?.focus();
  }, [isMobile, menuOpen]);

  useEffect(() => {
    if (!menuOpen || !isMobile) return;
    const handleMenuKeydown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        setMenuOpen(false);
        return;
      }
      if (event.key !== 'Tab' || !sidebarRef.current) return;
      const focusable = [
        ...sidebarRef.current.querySelectorAll<HTMLElement>('a[href], button:not(:disabled)'),
      ];
      const first = focusable[0];
      const last = focusable.at(-1);
      if (event.shiftKey && document.activeElement === first && last) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last && first) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', handleMenuKeydown);
    return () => document.removeEventListener('keydown', handleMenuKeydown);
  }, [isMobile, menuOpen]);

  const handleLogout = async () => {
    setHeaderError('');
    setLogoutPending(true);
    try {
      await logout();
      clearLastCommandId();
      setSession({ status: 'unauthenticated' });
      setLocations({ status: 'unauthenticated' });
      setPresets({ status: 'unauthenticated' });
      setLastCommand(null);
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        clearLastCommandId();
        setSession({ status: 'unauthenticated' });
        setLocations({ status: 'unauthenticated' });
        setPresets({ status: 'unauthenticated' });
      } else {
        setHeaderError('Не вдалося завершити сесію. Спробуйте ще раз.');
      }
    } finally {
      setLogoutPending(false);
    }
  };

  const handleCommand = async (selectedLocations: Location[]) => {
    if (
      !pendingCommand ||
      selectedLocations.length !== pendingCommand.locationIds.length ||
      selectedLocations.length === 0
    )
      return;
    const ids = selectedLocations.map((location) => location.id).sort();
    const fingerprint = JSON.stringify({
      presetCode: pendingCommand.preset.code,
      locationIds: ids,
    });
    if (!retryKeyRef.current || retryKeyRef.current.fingerprint !== fingerprint) {
      try {
        retryKeyRef.current = { fingerprint, key: crypto.randomUUID() };
      } catch {
        setCommandError(
          'Браузер не може створити безпечний ключ запиту. Оновіть сторінку або скористайтеся іншим браузером.',
        );
        return;
      }
    }

    setCommandPending(true);
    setCommandError('');
    try {
      const command = await createAlertCommand(
        pendingCommand.preset.code,
        ids,
        retryKeyRef.current.key,
      );
      setLastCommand(command);
      setLastCommandLocations(selectedLocations);
      saveLastCommandId(command.id);
      setLastCommandRestoreStatus('idle');
      retryKeyRef.current = null;
      setPendingCommand(null);
    } catch (error) {
      setCommandError(commandErrorMessage(error));
      if (error instanceof ApiError && error.status === 401) {
        setSession({ status: 'unauthenticated' });
        setLocations({ status: 'unauthenticated' });
        setPresets({ status: 'unauthenticated' });
      }
    } finally {
      setCommandPending(false);
    }
  };

  const handleStopCommand = async (commandId: string, selectedLocations: Location[]) => {
    if (
      !pendingStop ||
      commandId !== pendingStop.commandId ||
      selectedLocations.length !== pendingStop.locations.length ||
      selectedLocations.length === 0
    ) {
      return;
    }
    const ids = selectedLocations.map((location) => location.id).sort();
    const fingerprint = JSON.stringify({ action: 'stop', commandId, locationIds: ids });
    if (!retryKeyRef.current || retryKeyRef.current.fingerprint !== fingerprint) {
      try {
        retryKeyRef.current = { fingerprint, key: crypto.randomUUID() };
      } catch {
        setCommandError(
          'Браузер не може створити безпечний ключ запиту. Оновіть сторінку або скористайтеся іншим браузером.',
        );
        return;
      }
    }

    setCommandPending(true);
    setCommandError('');
    try {
      const command = await createStopCommand(commandId, ids, retryKeyRef.current.key);
      setLastCommand(command);
      setLastCommandLocations(selectedLocations);
      saveLastCommandId(command.id);
      setLastCommandRestoreStatus('idle');
      retryKeyRef.current = null;
      setPendingStop(null);
    } catch (error) {
      setCommandError(stopCommandErrorMessage(error));
      if (error instanceof ApiError && error.status === 401) {
        setSession({ status: 'unauthenticated' });
        setLocations({ status: 'unauthenticated' });
        setPresets({ status: 'unauthenticated' });
      }
    } finally {
      setCommandPending(false);
    }
  };

  const isAuthenticated = session.status === 'authenticated';
  const canOperate = isAuthenticated && session.capabilities.canOperate;
  const canViewAudit = isAuthenticated && session.capabilities.canViewAudit;
  const canManageUsers = isAuthenticated && session.roles.includes('admin');
  const canManagePresets = isAuthenticated && session.roles.includes('admin');
  const activeTitle =
    page === 'audit'
      ? 'Журнал дій'
      : page === 'users'
        ? 'Облікові записи'
        : page === 'presets'
          ? 'Звукові сценарії'
          : 'Пульт оповіщення';

  return (
    <div className={`app-shell${menuOpen && isMobile ? ' is-menu-open' : ''}`}>
      <aside className="sidebar" id="operator-sidebar" aria-label="Головне меню" ref={sidebarRef}>
        <a className="brand" href="#dashboard" onClick={() => setMenuOpen(false)}>
          <img src="/images/kpk-logo.png" alt="" />
          <span>
            Система оповіщення<small>Кременчуцький коледж</small>
          </span>
        </a>
        <nav className="sidebar-nav" aria-label="Розділи">
          {(canOperate || !isAuthenticated) && (
            <a
              className={`nav-link${page === 'dashboard' ? ' active' : ''}`}
              aria-current={page === 'dashboard' ? 'page' : undefined}
              href="#dashboard"
              onClick={() => setMenuOpen(false)}
            >
              <span className="nav-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24">
                  <path d="m3 10 9-7 9 7v10a1 1 0 0 1-1 1h-5v-7H9v7H4a1 1 0 0 1-1-1z" />
                </svg>
              </span>
              <span>Пульт керування</span>
            </a>
          )}
          {(canViewAudit || !isAuthenticated) && (
            <a
              className={`nav-link${page === 'audit' ? ' active' : ''}`}
              aria-current={page === 'audit' ? 'page' : undefined}
              href="#audit"
              onClick={() => setMenuOpen(false)}
            >
              <span className="nav-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24">
                  <path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01" />
                </svg>
              </span>
              <span>Журнал дій</span>
            </a>
          )}
          {canManageUsers && (
            <a
              className={`nav-link${page === 'users' ? ' active' : ''}`}
              aria-current={page === 'users' ? 'page' : undefined}
              href="#users"
              onClick={() => setMenuOpen(false)}
            >
              <span className="nav-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24">
                  <path d="M16 20v-1.5a4.5 4.5 0 0 0-4.5-4.5h-5A4.5 4.5 0 0 0 2 18.5V20m7-10a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm9 0a3.5 3.5 0 0 0 0-7m1 9h.5a4.5 4.5 0 0 1 4.5 4.5V20" />
                </svg>
              </span>
              <span>Облікові записи</span>
            </a>
          )}
          {canManagePresets && (
            <a
              className={`nav-link${page === 'presets' ? ' active' : ''}`}
              aria-current={page === 'presets' ? 'page' : undefined}
              href="#presets"
              onClick={() => setMenuOpen(false)}
            >
              <span className="nav-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24">
                  <path d="M4 5h16v14H4zM8 9h8M8 13h5M8 17h2" />
                </svg>
              </span>
              <span>Звукові сценарії</span>
            </a>
          )}
        </nav>
        <div className="sidebar-footer">
          <span className="sidebar-note">Команди створюються лише після явного підтвердження.</span>
        </div>
      </aside>

      {menuOpen && isMobile && (
        <button
          className="sidebar-backdrop"
          type="button"
          aria-label="Закрити меню"
          onClick={() => setMenuOpen(false)}
        />
      )}

      <main className="main-content" ref={mainRef}>
        <header className="topbar">
          <button
            ref={menuToggleRef}
            className="menu-toggle"
            type="button"
            aria-expanded={menuOpen}
            aria-controls="operator-sidebar"
            onClick={() => setMenuOpen((open) => !open)}
          >
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M4 6h16M4 12h16M4 18h16" />
            </svg>
            <span className="visually-hidden">{menuOpen ? 'Закрити меню' : 'Відкрити меню'}</span>
          </button>
          <a className="topbar-brand" href="#dashboard" aria-label="На пульт керування">
            <img src="/images/kpk-logo.png" alt="" />
          </a>
          <div className="breadcrumb">{activeTitle}</div>
          <div className="topbar-actions">
            <span className={`server-status status-${readiness}`} role="status" aria-live="polite">
              <span className="status-dot" aria-hidden="true" />
              {readinessLabel(readiness)}
            </span>
            {isAuthenticated ? (
              <button
                className="text-button"
                type="button"
                onClick={() => void handleLogout()}
                disabled={logoutPending}
              >
                {logoutPending ? 'Завершення…' : 'Вийти'}
              </button>
            ) : (
              <GoogleLoginLink className="login-link">Увійти через Google</GoogleLoginLink>
            )}
          </div>
        </header>

        <div className="page-content">
          {headerError && (
            <p className="alert alert-error" role="alert">
              {headerError}
            </p>
          )}
          {session.status === 'unauthenticated' && (
            <section className="session-banner" aria-labelledby="session-title">
              <div>
                <p className="eyebrow">СЕСІЯ</p>
                <h1 id="session-title">Увійдіть до системи</h1>
                <p>
                  Для роботи з корпусами та сценаріями потрібен обліковий запис Google, доданий
                  адміністратором.
                </p>
              </div>
              <GoogleLoginLink className="button button-primary">
                Увійти через Google
              </GoogleLoginLink>
            </section>
          )}
          {session.status === 'error' && (
            <section className="alert alert-error" role="alert">
              <strong>Не вдалося перевірити сесію.</strong>
              <span> Перевірте з’єднання та спробуйте ще раз.</span>
              <button
                className="button button-secondary"
                type="button"
                onClick={() => void refreshData()}
              >
                Повторити
              </button>
            </section>
          )}
          {session.status === 'forbidden' && (
            <section className="alert alert-error" role="alert">
              <strong>Обліковий запис не має доступу до системи.</strong>
              <span> Зверніться до адміністратора закладу.</span>
            </section>
          )}

          {page === 'dashboard' ? (
            <Dashboard
              session={session}
              locations={locations}
              presets={presets}
              lastCommand={lastCommand}
              commandLocations={lastCommandLocations}
              lastCommandRestoreStatus={lastCommandRestoreStatus}
              onRefresh={() => void refreshData()}
              onRun={(preset, locationIds) => {
                setCommandError('');
                setPendingStop(null);
                setPendingCommand({ preset, locationIds });
              }}
              onStop={(commandId, selectedLocations) => {
                setCommandError('');
                setPendingCommand(null);
                setPendingStop({ commandId, locations: selectedLocations });
              }}
            />
          ) : page === 'audit' ? (
            <AuditPage
              session={session}
              onSessionExpired={() => {
                setSession({ status: 'unauthenticated' });
                setLocations({ status: 'unauthenticated' });
                setPresets({ status: 'unauthenticated' });
              }}
              onSessionRetry={() => void refreshData()}
            />
          ) : page === 'users' && canManageUsers ? (
            <UserAccessPanel />
          ) : page === 'presets' && canManagePresets ? (
            <SoundPresetsPage
              session={session}
              onSessionExpired={() => {
                setSession({ status: 'unauthenticated' });
                setLocations({ status: 'unauthenticated' });
                setPresets({ status: 'unauthenticated' });
              }}
            />
          ) : null}

          <footer className="page-footer">
            <span>КГТА · Система оповіщення</span>
            <span>
              {offlineShell === 'ready'
                ? 'Офлайн-оболонку підготовлено'
                : offlineShell === 'unavailable'
                  ? 'Офлайн-оболонка недоступна'
                  : 'Перевірка офлайн-оболонки…'}
            </span>
          </footer>
        </div>
      </main>

      {pendingCommand && (
        <CommandDialog
          preset={pendingCommand.preset}
          locations={
            locations.status === 'ready'
              ? locations.data.filter((location) =>
                  pendingCommand.locationIds.includes(location.id),
                )
              : []
          }
          pending={commandPending}
          error={commandError}
          onCancel={() => {
            if (!commandPending) setPendingCommand(null);
          }}
          onConfirm={handleCommand}
        />
      )}
      {pendingStop && (
        <StopCommandDialog
          commandId={pendingStop.commandId}
          locations={pendingStop.locations}
          pending={commandPending}
          error={commandError}
          onCancel={() => {
            if (!commandPending) setPendingStop(null);
          }}
          onConfirm={handleStopCommand}
        />
      )}
    </div>
  );
}

export default App;
