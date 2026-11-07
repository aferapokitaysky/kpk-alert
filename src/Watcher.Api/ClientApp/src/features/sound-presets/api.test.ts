import { afterEach, describe, expect, it, vi } from 'vitest';
import { createSoundPreset, getSoundPresets, setSoundPresetPublication } from './api';

afterEach(() => vi.unstubAllGlobals());

describe('API звукових сценаріїв', () => {
  it('завантажує керований список без кешування браузером', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify([]), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    await expect(getSoundPresets()).resolves.toEqual([]);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/presets/manage',
      expect.objectContaining({
        cache: 'no-store',
        credentials: 'same-origin',
      }),
    );
  });

  it('передає CSRF-токен і точне тіло під час створення чернетки', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: 'csrf-value' }), { status: 200 }))
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            code: 'air_alert',
            name: 'Тривога',
            priority: 10,
            manifestHash: 'a'.repeat(64),
            isPublished: false,
          }),
          { status: 201 },
        ),
      );
    vi.stubGlobal('fetch', fetchMock);
    const payload = {
      code: 'air_alert',
      name: 'Тривога',
      priority: 10,
      manifestHash: 'a'.repeat(64),
    };
    await createSoundPreset(payload);
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/api/v1/presets',
      expect.objectContaining({
        method: 'POST',
        cache: 'no-store',
        credentials: 'same-origin',
        headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'csrf-value' }),
        body: JSON.stringify(payload),
      }),
    );
  });

  it('кодує код сценарію у шляху публікації', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: 'csrf-value' }), { status: 200 }))
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            code: 'air_alert',
            name: 'Тривога',
            priority: 10,
            manifestHash: 'a'.repeat(64),
            isPublished: true,
          }),
          { status: 200 },
        ),
      );
    vi.stubGlobal('fetch', fetchMock);
    await setSoundPresetPublication('air/alert', true);
    expect(fetchMock).toHaveBeenLastCalledWith(
      '/api/v1/presets/air%2Falert/publication',
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({ isPublished: true }),
      }),
    );
  });

  it('відхиляє відповідь із некоректним полем', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(new Response(JSON.stringify([{ code: 'bad' }]), { status: 200 })),
    );
    await expect(getSoundPresets()).rejects.toThrow('invalid-sound-preset-response');
  });
});
