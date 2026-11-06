import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { ApiError } from '../../api';
import type { SessionState } from '../../state';
import {
  createSoundPreset,
  getSoundPresets,
  setSoundPresetPublication,
  updateSoundPreset,
  type SoundPreset,
} from './api';
import './sound-presets.css';

type LoadState = 'loading' | 'ready' | 'unauthenticated' | 'forbidden' | 'error';
type Notice = { kind: 'success' | 'error'; text: string } | null;

interface Props {
  session: SessionState;
  onSessionExpired: () => void;
}

interface Draft {
  code: string;
  name: string;
  priority: string;
  manifestHash: string;
}

const emptyDraft: Draft = { code: '', name: '', priority: '100', manifestHash: '' };

function statusFromError(error: unknown): LoadState {
  if (error instanceof ApiError) {
    if (error.status === 401) return 'unauthenticated';
    if (error.status === 403) return 'forbidden';
  }
  return 'error';
}

function errorMessage(error: unknown): string {
  if (error instanceof ApiError && error.status === 409) {
    return 'Сценарій змінився або його опубліковано. Оновіть список і повторіть дію.';
  }
  if (error instanceof ApiError && error.status === 400) {
    return 'Перевірте код, назву та SHA-256 маніфесту.';
  }
  return 'Не вдалося зберегти зміни. Перевірте з’єднання та повторіть спробу.';
}

export function SoundPresetsPage({ session, onSessionExpired }: Props) {
  const isAdministrator = session.status === 'authenticated' && session.roles.includes('admin');
  const [presets, setPresets] = useState<SoundPreset[]>([]);
  const [loadState, setLoadState] = useState<LoadState>('loading');
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [editingCode, setEditingCode] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [pendingCode, setPendingCode] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice>(null);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      setLoadState('loading');
      try {
        const result = await getSoundPresets(signal);
        if (!signal?.aborted) {
          setPresets(result);
          setLoadState('ready');
        }
      } catch (error) {
        if (signal?.aborted) return;
        const status = statusFromError(error);
        setLoadState(status);
        if (status === 'unauthenticated') onSessionExpired();
      }
    },
    [onSessionExpired],
  );

  useEffect(() => {
    if (!isAdministrator) return;
    const controller = new AbortController();
    void Promise.resolve().then(() => {
      if (!controller.signal.aborted) void load(controller.signal);
    });
    return () => controller.abort();
  }, [isAdministrator, load]);

  function beginEdit(preset: SoundPreset) {
    setEditingCode(preset.code);
    setDraft({
      code: preset.code,
      name: preset.name,
      priority: String(preset.priority),
      manifestHash: preset.manifestHash,
    });
    setNotice(null);
  }

  function resetForm() {
    setDraft(emptyDraft);
    setEditingCode(null);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const priority = Number(draft.priority);
    if (!Number.isSafeInteger(priority)) {
      setNotice({ kind: 'error', text: 'Пріоритет має бути цілим числом.' });
      return;
    }
    setPending(true);
    setNotice(null);
    try {
      const payload = {
        code: draft.code.trim(),
        name: draft.name.trim(),
        priority,
        manifestHash: draft.manifestHash.trim(),
      };
      if (editingCode) {
        await updateSoundPreset(payload);
        setNotice({ kind: 'success', text: 'Зміни сценарію збережено.' });
      } else {
        await createSoundPreset(payload);
        setNotice({ kind: 'success', text: 'Сценарій створено як чернетку.' });
      }
      resetForm();
      await load();
    } catch (error) {
      const status = statusFromError(error);
      if (status === 'unauthenticated') onSessionExpired();
      setNotice({ kind: 'error', text: errorMessage(error) });
    } finally {
      setPending(false);
    }
  }

  async function togglePublication(preset: SoundPreset) {
    setPendingCode(preset.code);
    setNotice(null);
    try {
      await setSoundPresetPublication(preset.code, !preset.isPublished);
      setNotice({
        kind: 'success',
        text: preset.isPublished ? 'Публікацію сценарію знято.' : 'Сценарій опубліковано.',
      });
      await load();
    } catch (error) {
      const status = statusFromError(error);
      if (status === 'unauthenticated') onSessionExpired();
      setNotice({ kind: 'error', text: errorMessage(error) });
    } finally {
      setPendingCode(null);
    }
  }

  if (!isAdministrator) {
    return (
      <section className="sound-presets" aria-labelledby="sound-presets-title">
        <header className="sound-presets__heading">
          <div>
            <p className="eyebrow">Налаштування</p>
            <h1 id="sound-presets-title">Звукові сценарії</h1>
          </div>
        </header>
        <p className="sound-presets__state" role="status">
          Керування доступне лише глобальному адміністратору.
        </p>
      </section>
    );
  }

  return (
    <main className="sound-presets" aria-labelledby="sound-presets-title">
      <header className="sound-presets__heading">
        <div>
          <p className="eyebrow">Налаштування</p>
          <h1 id="sound-presets-title">Звукові сценарії</h1>
          <p>Код, пріоритет і SHA-256 маніфесту. Аудіофайли додаються окремо.</p>
        </div>
        <button
          className="btn btn-secondary sound-presets__refresh"
          type="button"
          onClick={() => void load()}
          disabled={loadState === 'loading'}
        >
          Оновити список
        </button>
      </header>

      {notice && (
        <p
          className={`sound-presets__notice sound-presets__notice--${notice.kind}`}
          role={notice.kind === 'error' ? 'alert' : 'status'}
        >
          {notice.text}
        </p>
      )}

      <div className="sound-presets__layout">
        <section className="sound-presets__list" aria-labelledby="sound-presets-list-title">
          <div className="sound-presets__section-heading">
            <h2 id="sound-presets-list-title">Сценарії</h2>
            {loadState === 'ready' && <span>{presets.length}</span>}
          </div>
          {loadState === 'loading' && (
            <p className="sound-presets__state" role="status">
              Завантаження сценаріїв…
            </p>
          )}
          {loadState === 'unauthenticated' && (
            <p className="sound-presets__state" role="alert">
              Сесію завершено. Увійдіть знову.
            </p>
          )}
          {loadState === 'forbidden' && (
            <p className="sound-presets__state" role="alert">
              Сервер не надав доступ до керування сценаріями.
            </p>
          )}
          {loadState === 'error' && (
            <div className="sound-presets__state" role="alert">
              <p>Не вдалося завантажити сценарії.</p>
              <button className="btn btn-secondary" type="button" onClick={() => void load()}>
                Повторити
              </button>
            </div>
          )}
          {loadState === 'ready' && presets.length === 0 && (
            <p className="sound-presets__state">Сценаріїв поки немає. Створіть перший.</p>
          )}
          {loadState === 'ready' && presets.length > 0 && (
            <ul className="sound-presets__items">
              {presets.map((preset) => (
                <li
                  className={`sound-presets__item${editingCode === preset.code ? ' sound-presets__item--selected' : ''}`}
                  key={preset.code}
                >
                  <div className="sound-presets__item-main">
                    <div>
                      <h3>{preset.name}</h3>
                      <code>{preset.code}</code>
                    </div>
                    <span
                      className={`sound-presets__badge${preset.isPublished ? ' sound-presets__badge--published' : ''}`}
                    >
                      {preset.isPublished ? 'Опубліковано' : 'Чернетка'}
                    </span>
                  </div>
                  <dl className="sound-presets__metadata">
                    <div>
                      <dt>Пріоритет</dt>
                      <dd>{preset.priority}</dd>
                    </div>
                    <div>
                      <dt>SHA-256</dt>
                      <dd>
                        <code title={preset.manifestHash}>{preset.manifestHash.slice(0, 12)}…</code>
                      </dd>
                    </div>
                  </dl>
                  <div className="sound-presets__actions">
                    {!preset.isPublished && (
                      <button
                        className="btn btn-secondary"
                        type="button"
                        onClick={() => beginEdit(preset)}
                      >
                        Редагувати
                      </button>
                    )}
                    <button
                      className="btn btn-secondary"
                      type="button"
                      disabled={pendingCode !== null}
                      onClick={() => void togglePublication(preset)}
                    >
                      {pendingCode === preset.code
                        ? 'Збереження…'
                        : preset.isPublished
                          ? 'Зняти публікацію'
                          : 'Опублікувати'}
                    </button>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="sound-presets__editor" aria-labelledby="sound-presets-form-title">
          <div className="sound-presets__section-heading">
            <h2 id="sound-presets-form-title">
              {editingCode ? 'Редагування сценарію' : 'Новий сценарій'}
            </h2>
          </div>
          <form onSubmit={(event) => void submit(event)}>
            <label className="sound-presets__field">
              <span>Код сценарію</span>
              <input
                required
                minLength={1}
                maxLength={80}
                pattern="[a-z0-9_]+"
                value={draft.code}
                disabled={pending || editingCode !== null}
                onChange={(event) => setDraft({ ...draft, code: event.target.value })}
                autoComplete="off"
              />
              <small>Латинські літери нижнього регістру, цифри та підкреслення.</small>
            </label>
            <label className="sound-presets__field">
              <span>Назва</span>
              <input
                required
                minLength={1}
                maxLength={160}
                value={draft.name}
                disabled={pending}
                onChange={(event) => setDraft({ ...draft, name: event.target.value })}
              />
            </label>
            <label className="sound-presets__field">
              <span>Пріоритет</span>
              <input
                required
                type="number"
                step="1"
                value={draft.priority}
                disabled={pending}
                onChange={(event) => setDraft({ ...draft, priority: event.target.value })}
              />
              <small>Менше число відображається вище у списку сценаріїв.</small>
            </label>
            <label className="sound-presets__field">
              <span>SHA-256 маніфесту</span>
              <input
                required
                minLength={64}
                maxLength={64}
                pattern="[A-Fa-f0-9]{64}"
                value={draft.manifestHash}
                disabled={pending}
                onChange={(event) => setDraft({ ...draft, manifestHash: event.target.value })}
                autoComplete="off"
                spellCheck={false}
              />
            </label>
            <div className="sound-presets__form-actions">
              <button className="btn btn-primary" type="submit" disabled={pending}>
                {pending ? 'Збереження…' : editingCode ? 'Зберегти зміни' : 'Створити чернетку'}
              </button>
              {editingCode && (
                <button
                  className="btn btn-secondary"
                  type="button"
                  disabled={pending}
                  onClick={resetForm}
                >
                  Скасувати
                </button>
              )}
            </div>
          </form>
          <p className="sound-presets__hint">
            Новий сценарій створюється неопублікованим. Щоб змінити опублікований, спочатку зніміть
            публікацію.
          </p>
        </section>
      </div>
    </main>
  );
}
