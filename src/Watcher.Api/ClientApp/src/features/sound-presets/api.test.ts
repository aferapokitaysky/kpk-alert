import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  createSoundPreset,
  deleteLocationAudioAsset,
  getLocationAudioAsset,
  getSoundPresets,
  setSoundPresetPublication,
  uploadLocationAudioAsset,
} from './api';

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

  it('читає відомості про аудіофайл і трактує 404 як відсутність файла', async () => {
    const asset = {
      assetId: 'asset-1',
      locationId: 'location-1',
      presetCode: 'air_alert',
      sha256: 'b'.repeat(64),
      sizeBytes: 2048,
      durationSeconds: 12,
    };
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(asset), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 404 }));
    vi.stubGlobal('fetch', fetchMock);
    await expect(getLocationAudioAsset('location-1', 'air_alert')).resolves.toEqual(asset);
    await expect(getLocationAudioAsset('location-1', 'air_alert')).resolves.toBeNull();
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      '/api/v1/admin/locations/location-1/sound-presets/air_alert/asset',
      expect.objectContaining({ cache: 'no-store', credentials: 'same-origin' }),
    );
  });

  it('завантажує файл як multipart і не задає Content-Type вручну', async () => {
    const asset = {
      assetId: 'asset-1',
      locationId: 'location-1',
      presetCode: 'air_alert',
      sha256: 'b'.repeat(64),
      sizeBytes: 2048,
      durationSeconds: 12,
    };
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: 'csrf-value' }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(asset), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const location = { id: 'location-1', name: 'Кременчук', code: 'kremenchuk' };
    const file = new File(['wav'], 'alarm.wav', { type: 'audio/wav' });

    await expect(uploadLocationAudioAsset(location, 'air_alert', file)).resolves.toEqual(asset);

    const options = fetchMock.mock.calls.at(1)?.[1];
    expect(options?.method).toBe('POST');
    expect(options?.headers).toEqual({ Accept: 'application/json', 'X-CSRF-TOKEN': 'csrf-value' });
    expect(options?.body).toBeInstanceOf(FormData);
    if (options?.body instanceof FormData) expect(options.body.get('File')).toBe(file);
  });

  it('видаляє аудіофайл із CSRF-заголовком', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: 'csrf-value' }), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(
      deleteLocationAudioAsset(
        { id: 'location-1', name: 'Кременчук', code: 'kremenchuk' },
        'air_alert',
      ),
    ).resolves.toBeUndefined();
    expect(fetchMock).toHaveBeenLastCalledWith(
      '/api/v1/admin/locations/location-1/sound-presets/air_alert/asset',
      expect.objectContaining({
        method: 'DELETE',
        headers: { Accept: 'application/json', 'X-CSRF-TOKEN': 'csrf-value' },
      }),
    );
  });
});
