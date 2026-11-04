import assert from 'node:assert/strict';
import test from 'node:test';
import { AlertCommandAttempt } from '../../src/Watcher.Api/wwwroot/js/alert-command-attempt.js';

test('повтор тієї самої команди зберігає ключ після втрати відповіді', () => {
  let sequence = 0;
  const attempt = new AlertCommandAttempt(() => `ключ-${++sequence}`);

  const firstKey = attempt.keyFor('evacuation', 'location-1');
  const retryKey = attempt.keyFor('evacuation', 'location-1');

  assert.equal(firstKey, 'ключ-1');
  assert.equal(retryKey, firstKey);
  assert.equal(sequence, 1);
});

test('інший сценарій або корпус отримує власний ключ', () => {
  let sequence = 0;
  const attempt = new AlertCommandAttempt(() => `ключ-${++sequence}`);

  assert.equal(attempt.keyFor('evacuation', 'location-1'), 'ключ-1');
  assert.equal(attempt.keyFor('air-raid', 'location-1'), 'ключ-2');
  assert.equal(attempt.keyFor('evacuation', 'location-2'), 'ключ-3');
});

test('після успіху наступний свідомий запуск отримує новий ключ', () => {
  let sequence = 0;
  const attempt = new AlertCommandAttempt(() => `ключ-${++sequence}`);

  const firstKey = attempt.keyFor('evacuation', 'location-1');
  attempt.complete('evacuation', 'location-1');

  assert.equal(attempt.keyFor('evacuation', 'location-1'), 'ключ-2');
  assert.notEqual(firstKey, 'ключ-2');
});
