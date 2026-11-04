import { AlertCommandAttempt } from './alert-command-attempt.js';

const dialog = document.querySelector('#confirm-dialog');
const locationSelect = document.querySelector('#location-select');
const feedback = document.querySelector('#feedback');
const confirmTitle = document.querySelector('#confirm-title');
const confirmCopy = document.querySelector('#confirm-copy');
const confirmSend = document.querySelector('#confirm-send');
let selectedPreset = null;
const commandAttempt = new AlertCommandAttempt();

function showFeedback(message, isError = false) {
  feedback.textContent = message;
  feedback.classList.toggle('is-error', isError);
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

document.querySelectorAll('[data-preset]').forEach((button) => {
  button.addEventListener('click', () => {
    if (!locationSelect.value) {
      showFeedback('Увійдіть і оберіть корпус перед запуском сценарію.', true);
      return;
    }
    selectedPreset = button.dataset.preset;
    confirmTitle.textContent = `Запустити сценарій «${button.querySelector('strong').textContent}»?`;
    confirmCopy.textContent = `Команда буде надіслана до корпусу «${locationSelect.selectedOptions[0].textContent}».`;
    dialog.showModal();
  });
});

confirmSend.addEventListener('click', async (event) => {
  event.preventDefault();
  if (!selectedPreset || !locationSelect.value) return;

  const presetCode = selectedPreset;
  const locationId = locationSelect.value;
  confirmSend.disabled = true;
  confirmSend.textContent = 'Надсилання…';
  try {
    const response = await fetch('/api/v1/alert-commands', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
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
