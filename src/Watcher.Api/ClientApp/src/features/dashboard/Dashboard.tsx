import { useState } from 'react';
import type { AlertCommand, Location, Preset } from '../../api';
import type { ResourceState, SessionState } from '../../state';

interface Props {
  session: SessionState;
  locations: ResourceState<Location[]>;
  presets: ResourceState<Preset[]>;
  lastCommand: AlertCommand | null;
  commandLocations: Location[];
  onRefresh: () => void;
  onRun: (preset: Preset, locationIds: string[]) => void;
}

function LocationChoices({
  state,
  selected,
  onChange,
  onRefresh,
}: {
  state: ResourceState<Location[]>;
  selected: string[];
  onChange: (ids: string[]) => void;
  onRefresh: () => void;
}) {
  if (state.status === 'loading')
    return (
      <div className="state-card" role="status">
        Завантажуємо доступні корпуси…
      </div>
    );
  if (state.status === 'unauthenticated')
    return <div className="state-card">Увійдіть, щоб переглянути корпуси.</div>;
  if (state.status === 'forbidden')
    return (
      <div className="state-card state-denied">
        У вас немає права переглядати корпуси для запуску.
      </div>
    );
  if (state.status === 'error') {
    return (
      <div className="state-card state-error" role="alert">
        <span>Не вдалося завантажити корпуси.</span>
        <button className="inline-button" type="button" onClick={onRefresh}>
          Повторити
        </button>
      </div>
    );
  }
  if (state.status === 'empty')
    return (
      <div className="state-card">Доступних корпусів немає. Зверніться до адміністратора.</div>
    );

  const toggle = (locationId: string) => {
    onChange(
      selected.includes(locationId)
        ? selected.filter((id) => id !== locationId)
        : [...selected, locationId],
    );
  };

  return (
    <fieldset className="location-grid" aria-describedby="location-help">
      <legend className="visually-hidden">Корпуси для команди</legend>
      {state.data.map((location) => (
        <label
          className={`location-option${selected.includes(location.id) ? ' is-selected' : ''}`}
          key={location.id}
        >
          <input
            type="checkbox"
            checked={selected.includes(location.id)}
            onChange={() => toggle(location.id)}
          />
          <span className="checkbox-mark" aria-hidden="true" />
          <span className="location-copy">
            <strong>{location.name}</strong>
            <small>{location.code}</small>
          </span>
        </label>
      ))}
      <span id="location-help" className="visually-hidden">
        Оберіть один або кілька корпусів.
      </span>
    </fieldset>
  );
}

function PresetState({
  state,
  onRefresh,
}: {
  state: ResourceState<Preset[]>;
  onRefresh: () => void;
}) {
  if (state.status === 'loading')
    return (
      <div className="state-card" role="status">
        Завантажуємо сценарії…
      </div>
    );
  if (state.status === 'unauthenticated')
    return <div className="state-card">Увійдіть, щоб переглянути сценарії.</div>;
  if (state.status === 'forbidden')
    return <div className="state-card state-denied">У вас немає права запускати сценарії.</div>;
  if (state.status === 'error') {
    return (
      <div className="state-card state-error" role="alert">
        <span>Не вдалося завантажити сценарії.</span>
        <button className="inline-button" type="button" onClick={onRefresh}>
          Повторити
        </button>
      </div>
    );
  }
  if (state.status === 'empty')
    return (
      <div className="state-card">
        Опублікованих сценаріїв поки немає. Зверніться до адміністратора.
      </div>
    );
  return null;
}

export function Dashboard({
  session,
  locations,
  presets,
  lastCommand,
  commandLocations,
  onRefresh,
  onRun,
}: Props) {
  const [selectedLocationIds, setSelectedLocationIds] = useState<string[]>([]);
  const selectedLocations =
    locations.status === 'ready'
      ? locations.data.filter((location) => selectedLocationIds.includes(location.id))
      : [];
  const canOperate = session.status === 'authenticated' && session.capabilities.canOperate;
  const canViewAudit = session.status === 'authenticated' && session.capabilities.canViewAudit;
  const canChooseScenario = selectedLocations.length > 0 && canOperate;

  return (
    <>
      <section className="page-heading">
        <div>
          <p className="eyebrow">КГТА · ОПОВІЩЕННЯ</p>
          <h1>Пульт керування</h1>
          <p className="lead">
            Оберіть сценарій і корпуси. Команда буде створена після підтвердження.
          </p>
        </div>
        <div className="page-heading-mark" aria-hidden="true">
          <span>К</span>
          <i />
        </div>
      </section>

      {session.status === 'authenticated' && !canOperate && (
        <div className="alert alert-info" role="status">
          {canViewAudit
            ? 'Ваш обліковий запис має доступ до перегляду, але не до запуску команд.'
            : 'Для вашого облікового запису не призначено прав у системі. Зверніться до адміністратора.'}
        </div>
      )}

      <section className="notice" aria-label="Перед запуском">
        <span className="notice-icon" aria-hidden="true">
          i
        </span>
        <p>
          <strong>Перевірте вибрані корпуси.</strong> Сервер прийме команду в чергу, але це не
          підтверджує відтворення звуку у вузлах.
        </p>
      </section>

      <section className="work-section" aria-labelledby="locations-title">
        <div className="section-heading">
          <div>
            <p className="eyebrow">01 · ЦІЛЬ</p>
            <h2 id="locations-title">Корпуси</h2>
          </div>
          {locations.status === 'ready' && (
            <span className="selection-count">Обрано: {selectedLocations.length}</span>
          )}
        </div>
        <LocationChoices
          state={locations}
          selected={selectedLocationIds}
          onChange={setSelectedLocationIds}
          onRefresh={onRefresh}
        />
      </section>

      <section className="work-section" aria-labelledby="presets-title">
        <div className="section-heading">
          <div>
            <p className="eyebrow">02 · ДІЯ</p>
            <h2 id="presets-title">Швидкий запуск</h2>
          </div>
          <button
            className="text-button"
            type="button"
            onClick={onRefresh}
            disabled={session.status === 'loading'}
          >
            Оновити дані
          </button>
        </div>
        {canOperate &&
          locations.status === 'ready' &&
          selectedLocations.length === 0 &&
          presets.status === 'ready' && (
            <p className="selection-hint">Оберіть хоча б один корпус, щоб продовжити.</p>
          )}
        <PresetState state={presets} onRefresh={onRefresh} />
        {presets.status === 'ready' && (
          <div className="preset-grid">
            {presets.data.map((preset, index) => (
              <button
                className="preset-card"
                key={preset.code}
                type="button"
                disabled={!canChooseScenario}
                onClick={() =>
                  onRun(
                    preset,
                    selectedLocations.map((location) => location.id),
                  )
                }
              >
                <span className="preset-index">{String(index + 1).padStart(2, '0')}</span>
                <span className="preset-copy">
                  <strong>{preset.name}</strong>
                  <small>Опублікований сценарій</small>
                </span>
                <span className="preset-arrow" aria-hidden="true">
                  →
                </span>
              </button>
            ))}
          </div>
        )}
      </section>

      {lastCommand && (
        <section className="command-result" aria-labelledby="result-title" role="status">
          <span className="result-mark" aria-hidden="true">
            ✓
          </span>
          <div>
            <p className="eyebrow">КОМАНДА ЗБЕРЕЖЕНА</p>
            <h2 id="result-title">Команду прийнято до черги</h2>
            <p>
              {commandLocations.map((location) => location.name).join(', ')} ·{' '}
              {new Date(lastCommand.createdAtUtc).toLocaleString('uk-UA')}
            </p>
            <small>Це не є підтвердженням відтворення звуку. Ідентифікатор: {lastCommand.id}</small>
          </div>
        </section>
      )}

      <section className="panel node-panel" aria-labelledby="nodes-title">
        <div className="panel-heading">
          <div>
            <p className="eyebrow">ЗВ’ЯЗОК З ОБЛАДНАННЯМ</p>
            <h2 id="nodes-title">Стан вузлів</h2>
          </div>
          <span className="unknown-pill">Немає даних</span>
        </div>
        <div className="unknown-state">
          <span className="unknown-mark" aria-hidden="true">
            ?
          </span>
          <div>
            <strong>Стан вузлів невідомий</strong>
            <p>У поточному API немає підтверджень стану пристроїв.</p>
          </div>
        </div>
      </section>
    </>
  );
}
