import { beforeEach, describe, expect, it } from 'vitest';
import { clearLastCommandId, readLastCommandId, saveLastCommandId } from './last-command-storage';

const storageKey = 'kgta-alert:last-command-id';
const commandId = '11111111-1111-4111-8111-111111111111';

beforeEach(() => window.sessionStorage.clear());

describe('посилання на останню команду', () => {
  it('зберігає лише UUID і читає його з поточної вкладки', () => {
    saveLastCommandId(commandId);

    expect(window.sessionStorage.length).toBe(1);
    expect(window.sessionStorage.getItem(storageKey)).toBe(commandId);
    expect(readLastCommandId()).toBe(commandId);
  });

  it('ігнорує некоректний UUID і видаляє старе некоректне значення', () => {
    saveLastCommandId('not-a-command-id');
    expect(window.sessionStorage.getItem(storageKey)).toBeNull();

    window.sessionStorage.setItem(storageKey, 'not-a-command-id');
    expect(readLastCommandId()).toBeNull();
    expect(window.sessionStorage.getItem(storageKey)).toBeNull();
  });

  it('видаляє збережену команду після виходу', () => {
    saveLastCommandId(commandId);
    clearLastCommandId();

    expect(readLastCommandId()).toBeNull();
  });
});
