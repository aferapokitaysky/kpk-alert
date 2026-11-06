import { ApiError, getCsrfToken } from '../../api';

export interface SoundPreset {
  code: string;
  name: string;
  priority: number;
  manifestHash: string;
  isPublished: boolean;
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
