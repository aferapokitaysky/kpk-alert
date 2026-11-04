import { AlertCommandAttempt } from './alert-command-attempt.js';

const dialog = document.querySelector('#confirm-dialog');
const locationSelect = document.querySelector('#location-select');
const feedback = document.querySelector('#feedback');
const confirmTitle = document.querySelector('#confirm-title');
const confirmCopy = document.querySelector('#confirm-copy');
const confirmSend = document.querySelector('#confirm-send');
const presetActions = document.querySelector('#preset-actions');
let selectedPreset = null;
const commandAttempt = new AlertCommandAttempt();
let csrfTokenPromise = null;

function showFeedback(message, isError = false) {
  feedback.textContent = message;
  feedback.classList.toggle('is-error', isError);
}

async function getCsrfToken() {
  if (!csrfTokenPromise) {
    csrfTokenPromise = fetch('/api/v1/auth/csrf-token').then(async (response) => {
      if (!response.ok) throw new Error('csrf-token-unavailable');
      const result = await response.json();
      if (!result.token) throw new Error('csrf-token-missing');
      return result.token;
    });
  }

  try {
    return await csrfTokenPromise;
  } catch (error) {
    csrfTokenPromise = null;
    throw error;
  }
}

async function loadLocations() {
  try {
    const response = await fetch('/api/v1/locations');
    if (response.status === 401) {
      locationSelect.innerHTML = '<option value="">Увійдіть, щоб переглянути корпуси</option>';
      return;
    }
    if (!response.ok) throw new Error('locations-unavailable');

    const locations = await response.json();
    locationSelect.replaceChildren(new Option('Оберіть корпус', ''));
    for (const location of locations) {
      locationSelect.add(new Option(location.name, location.id));
    }
    if (locations.length === 0) {
      locationSelect.replaceChildren(new Option('Немає доступних корпусів', ''));
    }
  } catch {
    locationSelect.replaceChildren(new Option('Не вдалося завантажити корпуси', ''));
    showFeedback('Не вдалося завантажити список корпусів. Оновіть сторінку або зверніться до адміністратора.', true);
  }
}

function createPresetButton(preset) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'action action-neutral';
  button.dataset.preset = preset.code;

  const icon = document.createElement('span');
  icon.className = 'action-icon';
  icon.textContent = '▶';

  const copy = document.createElement('span');
  const name = document.createElement('strong');
  name.textContent = preset.name;
  const description = document.createElement('small');
  description.textContent = 'Запуск сценарію';
  copy.append(name, description);

  const arrow = document.createElement('b');
  arrow.textContent = '›';
  button.append(icon, copy, arrow);
  button.addEventListener('click', () => {
    if (!locationSelect.value) {
      showFeedback('Увійдіть і оберіть корпус перед запуском сценарію.', true);
      return;
    }
    selectedPreset = preset.code;
    confirmTitle.textContent = `Запустити сценарій «${preset.name}»?`;
    confirmCopy.textContent = `Команда буде надіслана до корпусу «${locationSelect.selectedOptions[0].textContent}».`;
    dialog.showModal();
  });
  return button;
}

async function loadPresets() {
  try {
    const response = await fetch('/api/v1/presets');
    if (response.status === 401) {
      document.querySelector('#presets-empty').replaceChildren(document.createTextNode('Увійдіть, щоб переглянути доступні сценарії.'));
      return;
    }
    if (response.status === 403) {
      document.querySelector('#presets-empty').replaceChildren(document.createTextNode('У вас немає права переглядати сценарії.'));
      return;
    }
    if (!response.ok) throw new Error('presets-unavailable');

    const presets = await response.json();
    presetActions.replaceChildren(...presets.map(createPresetButton));
    presetActions.hidden = presets.length === 0;
    document.querySelector('#presets-empty').hidden = presets.length > 0;
  } catch {
    document.querySelector('#presets-empty').replaceChildren(document.createTextNode('Не вдалося завантажити сценарії. Оновіть сторінку або зверніться до адміністратора.'));
  }
}

confirmSend.addEventListener('click', async (event) => {
  event.preventDefault();
  if (!selectedPreset || !locationSelect.value) return;

  const presetCode = selectedPreset;
  const locationId = locationSelect.value;
  confirmSend.disabled = true;
  confirmSend.textContent = 'Надсилання…';
  try {
    const csrfToken = await getCsrfToken();
    const response = await fetch('/api/v1/alert-commands', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': csrfToken,
        'Idempotency-Key': commandAttempt.keyFor(presetCode, locationId)
      },
      body: JSON.stringify({
        action: 'start',
        presetCode,
        locationIds: [locationId]
      })
    });
    if (response.status === 401) {
      showFeedback('Сесію завершено. Увійдіть знову, щоб продовжити.', true);
      return;
    }
    if (response.status === 403) {
      showFeedback('У вас немає права запускати сценарій для цього корпусу.', true);
      return;
    }
    if (!response.ok) throw new Error('command-rejected');

    commandAttempt.complete(presetCode, locationId);
    dialog.close();
    showFeedback('Команду записано. Стан відтворення буде показано після підтвердження від вузла.');
  } catch {
    showFeedback('Не вдалося записати команду. Перевірте з’єднання та повторіть спробу.', true);
  } finally {
    confirmSend.disabled = false;
    confirmSend.textContent = 'Підтвердити запуск';
  }
});

loadLocations();
loadPresets();
