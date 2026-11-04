import assert from 'node:assert/strict';
import test from 'node:test';
import { healthState, listState, sidebarIsInert } from '../../src/Watcher.Api/wwwroot/js/ui-state.js';

test('список корпусів розрізняє стан завантаження, доступу й даних', () => {
  assert.equal(listState({ status: null, count: 0 }), 'loading');
  assert.equal(listState({ status: 401, count: 0 }), 'unauthenticated');
  assert.equal(listState({ status: 403, count: 0 }), 'forbidden');
  assert.equal(listState({ status: 200, count: 0 }), 'empty');
  assert.equal(listState({ status: 200, count: 2 }), 'ready');
  assert.equal(listState({ status: 503, count: 0 }), 'error');
});

test('стан сервера доступний лише для успішної readiness-відповіді', () => {
  assert.equal(healthState(200, { status: 'ok' }), 'available');
  assert.equal(healthState(200, { status: 'degraded' }), 'unavailable');
  assert.equal(healthState(503, { status: 'ok' }), 'unavailable');
  assert.equal(healthState(0, null), 'unavailable');
});

test('закрите мобільне меню вилучене з фокусу, відкрите та desktop-меню доступні', () => {
  assert.equal(sidebarIsInert(true, false), true);
  assert.equal(sidebarIsInert(true, true), false);
  assert.equal(sidebarIsInert(false, false), false);
});
