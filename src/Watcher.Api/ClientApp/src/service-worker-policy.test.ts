import { describe, expect, it } from 'vitest';
import { isCacheableStaticRequest } from './service-worker-policy';

describe('політика кешування PWA', () => {
  it('кешує лише статичну оболонку того самого походження', () => {
    expect(
      isCacheableStaticRequest(
        'GET',
        'https://notify.example/dist/operator.js',
        'https://notify.example',
      ),
    ).toBe(true);
    expect(
      isCacheableStaticRequest(
        'GET',
        'https://notify.example/images/kpk-logo-maskable.png',
        'https://notify.example',
      ),
    ).toBe(true);
    expect(
      isCacheableStaticRequest(
        'GET',
        'https://notify.example/api/v1/locations',
        'https://notify.example',
      ),
    ).toBe(false);
    expect(
      isCacheableStaticRequest(
        'GET',
        'https://notify.example/api/v1/alert-commands',
        'https://notify.example',
      ),
    ).toBe(false);
    expect(
      isCacheableStaticRequest(
        'POST',
        'https://notify.example/dist/operator.js',
        'https://notify.example',
      ),
    ).toBe(false);
    expect(
      isCacheableStaticRequest('GET', 'https://cdn.example/operator.js', 'https://notify.example'),
    ).toBe(false);
    expect(
      isCacheableStaticRequest('GET', 'https://notify.example/admin', 'https://notify.example'),
    ).toBe(false);
  });
});
