const storageKey = 'kgta-alert:last-command-id';
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function readLastCommandId(): string | null {
  try {
    const commandId = window.sessionStorage.getItem(storageKey);
    if (!commandId) return null;
    if (guidPattern.test(commandId)) return commandId;

    window.sessionStorage.removeItem(storageKey);
  } catch {
    return null;
  }

  return null;
}

export function saveLastCommandId(commandId: string): void {
  if (!guidPattern.test(commandId)) return;

  try {
    window.sessionStorage.setItem(storageKey, commandId);
  } catch {
    // Стан пульта має працювати й тоді, коли браузер вимкнув сховище вкладки.
  }
}

export function clearLastCommandId(): void {
  try {
    window.sessionStorage.removeItem(storageKey);
  } catch {
    // Відсутність сховища не повинна блокувати вихід із системи.
  }
}
