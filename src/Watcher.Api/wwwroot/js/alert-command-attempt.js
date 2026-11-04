export class AlertCommandAttempt {
  #keysByPayload = new Map();
  #createKey;

  constructor(createKey = () => crypto.randomUUID()) {
    this.#createKey = createKey;
  }

  keyFor(presetCode, locationId) {
    const payload = JSON.stringify({ action: 'start', presetCode, locationId });
    let key = this.#keysByPayload.get(payload);
    if (!key) {
      key = this.#createKey();
      this.#keysByPayload.set(payload, key);
    }
    return key;
  }

  complete(presetCode, locationId) {
    this.#keysByPayload.delete(JSON.stringify({ action: 'start', presetCode, locationId }));
  }
}
