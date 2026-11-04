import { AlertCommandAttempt } from './alert-command-attempt.js';
import { healthState, listState, sidebarIsInert } from './ui-state.js';

const dialog = document.querySelector('#confirm-dialog');
const locationSelect = document.querySelector('#location-select');
const feedback = document.querySelector('#feedback');
const confirmTitle = document.querySelector('#confirm-title');
const confirmCopy = document.querySelector('#confirm-copy');
const confirmSend = document.querySelector('#confirm-send');
const presetActions = document.querySelector('#preset-actions');
const presetsEmpty = document.querySelector('#presets-empty');
const dialogFeedback = document.querySelector('#dialog-feedback');
const serverHealth = document.querySelector('#server-health');
const menuToggle = document.querySelector('#menu-toggle');
const navBackdrop = document.querySelector('#nav-backdrop');
const sidebar = document.querySelector('#sidebar');
const mobileViewport = window.matchMedia('(max-width: 820px)');
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
    const locations = response.ok ? await response.json() : [];
    const state = listState({ status: response.status, count: locations.length });
    locationSelect.setAttribute('aria-busy', 'false');
    locationSelect.disabled = state !== 'ready';
    const messages = {
      unauthenticated: 'Увійдіть, щоб переглянути корпуси',
      forbidden: 'Немає доступу до корпусів',
      empty: 'Доступних корпусів немає',
      error: 'Не вдалося завантажити корпуси'
    };
    if (state !== 'ready') {
      locationSelect.replaceChildren(new Option(messages[state], ''));
      if (state === 'error') showFeedback('Не вдалося завантажити список корпусів. Оновіть сторінку або зверніться до адміністратора.', true);
      return;
    }

    locationSelect.replaceChildren(new Option('Оберіть корпус', ''));
    for (const location of locations) {
      locationSelect.add(new Option(location.name, location.id));
    }
  } catch {
    locationSelect.setAttribute('aria-busy', 'false');
    locationSelect.disabled = true;
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
    const presets = response.ok ? await response.json() : [];
    const state = listState({ status: response.status, count: presets.length });
    presetsEmpty.setAttribute('aria-busy', 'false');
    const messages = {
      unauthenticated: 'Увійдіть, щоб переглянути доступні сценарії.',
      forbidden: 'У вас немає права переглядати сценарії.',
      empty: 'Опублікованих сценаріїв поки немає. Зверніться до адміністратора, щоб налаштувати доступні сценарії.',
      error: 'Не вдалося завантажити сценарії. Оновіть сторінку або зверніться до адміністратора.'
    };
    if (state !== 'ready') {
      presetsEmpty.replaceChildren(document.createTextNode(messages[state]));
      presetActions.hidden = true;
      presetsEmpty.hidden = false;
      return;
    }
    presetActions.replaceChildren(...presets.map(createPresetButton));
    presetActions.hidden = presets.length === 0;
    presetsEmpty.hidden = presets.length > 0;
  } catch {
    presetsEmpty.setAttribute('aria-busy', 'false');
    presetsEmpty.replaceChildren(document.createTextNode('Не вдалося завантажити сценарії. Оновіть сторінку або зверніться до адміністратора.'));
    presetActions.hidden = true;
    presetsEmpty.hidden = false;
  }
}

async function checkServerHealth() {
  try {
    const response = await fetch('/api/v1/health/ready', { cache: 'no-store' });
    const payload = response.ok ? await response.json() : null;
    const state = healthState(response.status, payload);
    serverHealth.dataset.state = state;
    serverHealth.lastElementChild.textContent = state === 'available' ? 'Сервер доступний' : 'Сервер недоступний';
  } catch {
    serverHealth.dataset.state = 'unavailable';
    serverHealth.lastElementChild.textContent = 'Сервер недоступний';
  }
}

function setMenuOpen(open, restoreFocus = true) {
  const isOpen = mobileViewport.matches && open;
  menuToggle.setAttribute('aria-expanded', String(isOpen));
  navBackdrop.hidden = !isOpen;
  document.body.classList.toggle('nav-open', isOpen);
  sidebar.inert = sidebarIsInert(mobileViewport.matches, isOpen);
  if (mobileViewport.matches) sidebar.setAttribute('aria-hidden', String(!isOpen));
  else sidebar.removeAttribute('aria-hidden');
  if (isOpen) document.querySelector('.sidebar .nav-link')?.focus();
  else if (restoreFocus && sidebar.contains(document.activeElement)) menuToggle.focus();
}

menuToggle.addEventListener('click', () => setMenuOpen(menuToggle.getAttribute('aria-expanded') !== 'true'));
navBackdrop.addEventListener('click', () => setMenuOpen(false));
document.querySelector('.sidebar .nav-link').addEventListener('click', () => {
  if (window.matchMedia('(max-width: 820px)').matches) setMenuOpen(false);
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && menuToggle.getAttribute('aria-expanded') === 'true') setMenuOpen(false);
});
mobileViewport.addEventListener('change', () => setMenuOpen(false, mobileViewport.matches));
dialog.addEventListener('close', () => {
  dialogFeedback.hidden = true;
  dialogFeedback.textContent = '';
});

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
      dialogFeedback.textContent = 'Сесію завершено. Увійдіть знову, щоб продовжити.';
      dialogFeedback.hidden = false;
      return;
    }
    if (response.status === 403) {
      dialogFeedback.textContent = 'У вас немає права запускати сценарій для цього корпусу.';
      dialogFeedback.hidden = false;
      return;
    }
    if (!response.ok) throw new Error('command-rejected');

    commandAttempt.complete(presetCode, locationId);
    dialog.close();
    showFeedback('Команду записано. Стан відтворення буде показано після підтвердження від вузла.');
  } catch {
    dialogFeedback.textContent = 'Не вдалося записати команду. Перевірте з’єднання та повторіть спробу.';
    dialogFeedback.hidden = false;
  } finally {
    confirmSend.disabled = false;
    confirmSend.textContent = 'Підтвердити запуск';
  }
});

loadLocations();
loadPresets();
setMenuOpen(false, false);
checkServerHealth();
window.setInterval(checkServerHealth, 30000);
