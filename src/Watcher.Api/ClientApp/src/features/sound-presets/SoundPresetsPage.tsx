import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { ApiError } from '../../api';
import type { Location } from '../../api';
import type { SessionState } from '../../state';
import {
  createSoundPreset,
  deleteLocationAudioAsset,
  getLocationAudioAsset,
  getSoundPresets,
  setSoundPresetPublication,
  uploadLocationAudioAsset,
  updateSoundPreset,
  type LocationAudioAsset,
  type SoundPreset,
} from './api';
import './sound-presets.css';

type LoadState = 'loading' | 'ready' | 'unauthenticated' | 'forbidden' | 'error';
type Notice = { kind: 'success' | 'error'; text: string } | null;

interface Props {
  session: SessionState;
  locations?: Location[];
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

export function SoundPresetsPage({ session, locations = [], onSessionExpired }: Props) {
  const isAdministrator = session.status === 'authenticated' && session.roles.includes('admin');
  const [presets, setPresets] = useState<SoundPreset[]>([]);
  const [loadState, setLoadState] = useState<LoadState>('loading');
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [editingCode, setEditingCode] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [pendingCode, setPendingCode] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice>(null);
  const [selectedLocationId, setSelectedLocationId] = useState('');
  const [selectedAudioPresetCode, setSelectedAudioPresetCode] = useState('');
  const [audioLookup, setAudioLookup] = useState<{
    key: string;
    asset: LocationAudioAsset | null;
    state: LoadState;
  } | null>(null);
  const [audioPending, setAudioPending] = useState(false);
  const selectedLocation =
    locations.find((location) => location.id === selectedLocationId) ?? locations.at(0);
  const selectedAudioPreset =
    presets.find((preset) => preset.code === selectedAudioPresetCode) ?? presets.at(0);
  const audioLookupKey =
    selectedLocation && selectedAudioPreset
      ? `${selectedLocation.id}:${selectedAudioPreset.code}`
      : null;
  const audioState = audioLookupKey
    ? audioLookup?.key === audioLookupKey
      ? audioLookup.state
      : 'loading'
    : 'ready';
  const audioAsset = audioLookup?.key === audioLookupKey ? audioLookup.asset : null;

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

  useEffect(() => {
    if (!selectedLocation || !selectedAudioPreset || !audioLookupKey) {
      return;
    }

    const controller = new AbortController();
    void getLocationAudioAsset(selectedLocation.id, selectedAudioPreset.code, controller.signal)
      .then((asset) => {
        if (controller.signal.aborted) return;
        setAudioLookup({ key: audioLookupKey, asset, state: 'ready' });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        const status = statusFromError(error);
        setAudioLookup({ key: audioLookupKey, asset: null, state: status });
        if (status === 'unauthenticated') onSessionExpired();
      });
    return () => controller.abort();
  }, [audioLookupKey, onSessionExpired, selectedAudioPreset, selectedLocation]);

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

  async function uploadAudio(file: File) {
    if (!selectedLocation || !selectedAudioPreset) return;
    const lookupKey = `${selectedLocation.id}:${selectedAudioPreset.code}`;
    if (file.size > 100 * 1024 * 1024) {
      setNotice({ kind: 'error', text: 'Файл перевищує обмеження 100 MiB.' });
      return;
    }
    setAudioPending(true);
    setNotice(null);
    try {
      const result = await uploadLocationAudioAsset(
        selectedLocation,
        selectedAudioPreset.code,
        file,
      );
      setAudioLookup({ key: lookupKey, asset: result, state: 'ready' });
      setNotice({ kind: 'success', text: 'Аудіофайл завантажено та перевірено.' });
    } catch (error) {
      const status = statusFromError(error);
      if (status === 'unauthenticated') onSessionExpired();
      const text =
        error instanceof ApiError && error.status === 409
          ? 'Спочатку зніміть публікацію сценарію, потім замініть аудіофайл.'
          : error instanceof ApiError && error.status === 400
            ? 'Файл не пройшов перевірку. Потрібен коректний PCM WAV тривалістю до 15 хвилин.'
            : 'Не вдалося завантажити файл. Перевірте з’єднання та повторіть спробу.';
      setNotice({ kind: 'error', text });
    } finally {
      setAudioPending(false);
    }
  }

  async function deleteAudio() {
    if (!selectedLocation || !selectedAudioPreset || !audioAsset) return;
    const lookupKey = `${selectedLocation.id}:${selectedAudioPreset.code}`;
    setAudioPending(true);
    setNotice(null);
    try {
      await deleteLocationAudioAsset(selectedLocation, selectedAudioPreset.code);
      setAudioLookup({ key: lookupKey, asset: null, state: 'ready' });
      setNotice({ kind: 'success', text: 'Аудіофайл видалено.' });
    } catch (error) {
      const status = statusFromError(error);
      if (status === 'unauthenticated') onSessionExpired();
      setNotice({ kind: 'error', text: errorMessage(error) });
    } finally {
      setAudioPending(false);
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

      <section className="sound-presets__audio" aria-labelledby="sound-presets-audio-title">
        <div className="sound-presets__section-heading">
          <div>
            <h2 id="sound-presets-audio-title">Аудіофайл сценарію</h2>
            <p>Пакет зберігається окремо для кожної локації.</p>
          </div>
        </div>
        {locations.length === 0 ? (
          <p className="sound-presets__state">Немає доступних локацій для налаштування.</p>
        ) : presets.length === 0 ? (
          <p className="sound-presets__state">Спочатку створіть сценарій.</p>
        ) : (
          <div className="sound-presets__audio-layout">
            <label className="sound-presets__field">
              <span>Локація</span>
              <select
                value={selectedLocationId}
                onChange={(event) => setSelectedLocationId(event.target.value)}
                disabled={audioPending}
              >
                {locations.map((location) => (
                  <option key={location.id} value={location.id}>
                    {location.name}
                  </option>
                ))}
              </select>
            </label>
            <label className="sound-presets__field">
              <span>Сценарій</span>
              <select
                value={selectedAudioPresetCode}
                onChange={(event) => setSelectedAudioPresetCode(event.target.value)}
                disabled={audioPending}
              >
                {presets.map((preset) => (
                  <option key={preset.code} value={preset.code}>
                    {preset.name} · {preset.code}
                  </option>
                ))}
              </select>
            </label>
            <div className="sound-presets__audio-details" aria-live="polite">
              {audioState === 'loading' ? (
                <p className="sound-presets__state">Перевірка аудіофайла…</p>
              ) : audioState === 'error' || audioState === 'forbidden' ? (
                <p className="sound-presets__state" role="alert">
                  Не вдалося перевірити стан аудіофайла.
                </p>
              ) : audioAsset ? (
                <p className="sound-presets__state">
                  Файл додано · {Math.ceil(audioAsset.sizeBytes / 1024)} KiB ·{' '}
                  {audioAsset.durationSeconds} с · SHA-256{' '}
                  <code>{audioAsset.sha256.slice(0, 12)}…</code>
                </p>
              ) : (
                <p className="sound-presets__state">
                  Для цієї пари локації та сценарію файлу ще немає.
                </p>
              )}
            </div>
            <label className="sound-presets__field">
              <span>{audioAsset ? 'Замінити WAV-файл' : 'Завантажити WAV-файл'}</span>
              <input
                type="file"
                accept=".wav,audio/wav"
                disabled={
                  audioPending ||
                  !selectedLocation ||
                  !selectedAudioPreset ||
                  selectedAudioPreset.isPublished
                }
                onChange={(event) => {
                  const file = event.currentTarget.files?.[0];
                  event.currentTarget.value = '';
                  if (file) void uploadAudio(file);
                }}
              />
              <small>PCM WAV, до 100 MiB, тривалість до 15 хвилин.</small>
            </label>
            {selectedAudioPreset?.isPublished && (
              <p className="sound-presets__hint">Щоб змінити аудіо, зніміть публікацію сценарію.</p>
            )}
            {audioAsset && (
              <button
                className="button-danger"
                type="button"
                disabled={
                  audioPending || audioState !== 'ready' || selectedAudioPreset?.isPublished
                }
                onClick={() => void deleteAudio()}
              >
                {audioPending ? 'Збереження…' : 'Видалити аудіофайл'}
              </button>
            )}
          </div>
        )}
      </section>
    </main>
  );
}
