import { ApiError } from './api';
import { describe, expect, it } from 'vitest';
import { commandErrorMessage, pageCount, readinessFromResponse, resourceFromResult } from './state';

describe('стани даних пульта', () => {
  it('розділяє порожню відповідь, відмову та помилку списку', () => {
    expect(
      resourceFromResult(
        { status: 'fulfilled', value: [] },
        (items: string[]) => items.length === 0,
      ),
    ).toEqual({ status: 'empty' });
    expect(
      resourceFromResult({ status: 'rejected', reason: new ApiError(401) }, () => false),
    ).toEqual({ status: 'unauthenticated' });
    expect(
      resourceFromResult({ status: 'rejected', reason: new ApiError(403) }, () => false),
    ).toEqual({ status: 'forbidden' });
    expect(
      resourceFromResult({ status: 'rejected', reason: new Error('offline') }, () => false),
    ).toEqual({ status: 'error' });
  });

  it('показує сервер доступним тільки за успішної readiness-відповіді', () => {
    expect(readinessFromResponse(200, 'ok')).toBe('available');
    expect(readinessFromResponse(503, 'unavailable')).toBe('unavailable');
    expect(readinessFromResponse(404, null)).toBe('unknown');
    expect(readinessFromResponse(200, null)).toBe('unknown');
  });

  it('рахує сторінки й пояснює типові відмови запуску', () => {
    expect(pageCount(0, 25)).toBe(1);
    expect(pageCount(51, 25)).toBe(3);
    expect(commandErrorMessage(new ApiError(409))).toContain('ключ');
    expect(commandErrorMessage(new ApiError(403))).toContain('права');
  });
});
