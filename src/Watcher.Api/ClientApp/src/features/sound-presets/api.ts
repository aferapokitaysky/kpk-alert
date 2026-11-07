import { ApiError, getCsrfToken } from '../../api';
import type { Location } from '../../api';

export interface SoundPreset {
  code: string;
  name: string;
  priority: number;
  manifestHash: string;
  isPublished: boolean;
}

export interface LocationAudioAsset {
  assetId: string;
  locationId: string;
  presetCode: string;
  sha256: string;
  sizeBytes: number;
  durationSeconds: number;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parsePreset(value: unknown): SoundPreset {
  if (
    !isRecord(value) ||
    typeof value.code !== 'string' ||
    typeof value.name !== 'string' ||
    typeof value.priority !== 'number' ||
    !Number.isInteger(value.priority) ||
    typeof value.manifestHash !== 'string' ||
    typeof value.isPublished !== 'boolean'
  ) {
    throw new Error('invalid-sound-preset-response');
  }
  return {
    code: value.code,
    name: value.name,
    priority: value.priority,
    manifestHash: value.manifestHash,
    isPublished: value.isPublished,
  };
}

async function readPreset(response: Response): Promise<SoundPreset> {
  if (!response.ok) throw new ApiError(response.status);
  return parsePreset(await response.json());
}

export async function getSoundPresets(signal?: AbortSignal): Promise<SoundPreset[]> {
  const response = await fetch('/api/v1/presets/manage', {
    cache: 'no-store',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
    ...(signal ? { signal } : {}),
  });
  if (!response.ok) throw new ApiError(response.status);
  const result: unknown = await response.json();
  if (!Array.isArray(result)) throw new Error('invalid-sound-presets-response');
  return result.map(parsePreset);
}

async function mutate(path: string, method: string, body: unknown): Promise<SoundPreset> {
  const token = await getCsrfToken();
  const response = await fetch(path, {
    method,
    cache: 'no-store',
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': token,
    },
    body: JSON.stringify(body),
  });
  return readPreset(response);
}

export function createSoundPreset(preset: Omit<SoundPreset, 'isPublished'>): Promise<SoundPreset> {
  return mutate('/api/v1/presets', 'POST', preset);
}

export function updateSoundPreset(
  preset: Pick<SoundPreset, 'code' | 'name' | 'priority' | 'manifestHash'>,
): Promise<SoundPreset> {
  const { code, ...body } = preset;
  return mutate(`/api/v1/presets/${encodeURIComponent(code)}`, 'PUT', body);
}

export function setSoundPresetPublication(
  code: string,
  isPublished: boolean,
): Promise<SoundPreset> {
  return mutate(`/api/v1/presets/${encodeURIComponent(code)}/publication`, 'PUT', {
    isPublished,
  });
}

export async function getLocationAudioAsset(
  locationId: string,
  presetCode: string,
  signal?: AbortSignal,
): Promise<LocationAudioAsset | null> {
  const response = await fetch(
    `/api/v1/admin/locations/${encodeURIComponent(locationId)}/sound-presets/${encodeURIComponent(presetCode)}/asset`,
    {
      cache: 'no-store',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
      ...(signal ? { signal } : {}),
    },
  );
  if (response.status === 404) return null;
  if (!response.ok) throw new ApiError(response.status);
  return parseAudioAsset(await response.json(), locationId, presetCode);
}

export async function uploadLocationAudioAsset(
  location: Location,
  presetCode: string,
  file: File,
): Promise<LocationAudioAsset> {
  const token = await getCsrfToken();
  const formData = new FormData();
  formData.append('File', file);
  const response = await fetch(
    `/api/v1/admin/locations/${encodeURIComponent(location.id)}/sound-presets/${encodeURIComponent(presetCode)}/asset`,
    {
      method: 'POST',
      cache: 'no-store',
      credentials: 'same-origin',
      headers: { Accept: 'application/json', 'X-CSRF-TOKEN': token },
      body: formData,
    },
  );
  if (!response.ok) throw new ApiError(response.status);
  return parseAudioAsset(await response.json(), location.id, presetCode);
}

export async function deleteLocationAudioAsset(
  location: Location,
  presetCode: string,
): Promise<void> {
  const token = await getCsrfToken();
  const response = await fetch(
    `/api/v1/admin/locations/${encodeURIComponent(location.id)}/sound-presets/${encodeURIComponent(presetCode)}/asset`,
    {
      method: 'DELETE',
      cache: 'no-store',
      credentials: 'same-origin',
      headers: { Accept: 'application/json', 'X-CSRF-TOKEN': token },
    },
  );
  if (!response.ok) throw new ApiError(response.status);
}

function parseAudioAsset(
  value: unknown,
  locationId: string,
  presetCode: string,
): LocationAudioAsset {
  if (
    !isRecord(value) ||
    typeof value.assetId !== 'string' ||
    value.locationId !== locationId ||
    value.presetCode !== presetCode ||
    typeof value.sha256 !== 'string' ||
    typeof value.sizeBytes !== 'number' ||
    typeof value.durationSeconds !== 'number'
  ) {
    throw new Error('invalid-location-audio-asset-response');
  }
  return {
    assetId: value.assetId,
    locationId,
    presetCode,
    sha256: value.sha256,
    sizeBytes: value.sizeBytes,
    durationSeconds: value.durationSeconds,
  };
}
