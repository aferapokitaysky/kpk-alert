import { ApiError, getCsrfToken } from '../../api';

export interface DeviceStatus {
  deviceId: string;
  name: string;
  locationName: string;
  locationIsActive: boolean;
  isActive: boolean;
  isConnected: boolean;
  lastSeenAtUtc: string | null;
}

export interface DeviceStatusPage {
  items: DeviceStatus[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export class DeviceStatusApiError extends Error {
  constructor(readonly status: number) {
    super(`Device status API failed with status ${status}`);
    this.name = 'DeviceStatusApiError';
  }
}

export interface RegisterDeviceInput {
  name: string;
  locationId: string;
  certificateSha256: string;
}

export interface DeviceMutationResult {
  deviceId: string;
  isActive: boolean;
  revokedAtUtc?: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parseDeviceStatusPage(value: unknown): DeviceStatusPage {
  if (!isRecord(value) || !Array.isArray(value.items)) throw new Error('invalid-device-status');
  const { page, pageSize, totalCount } = value;
  if (
    !Number.isInteger(page) ||
    !Number.isInteger(pageSize) ||
    !Number.isInteger(totalCount) ||
    (page as number) < 1 ||
    (pageSize as number) < 1 ||
    (totalCount as number) < 0
  ) {
    throw new Error('invalid-device-status');
  }

  const items = value.items.map((item): DeviceStatus => {
    if (
      !isRecord(item) ||
      typeof item.deviceId !== 'string' ||
      typeof item.name !== 'string' ||
      typeof item.locationName !== 'string' ||
      typeof item.locationIsActive !== 'boolean' ||
      typeof item.isActive !== 'boolean' ||
      typeof item.isConnected !== 'boolean' ||
      (item.lastSeenAtUtc !== null && typeof item.lastSeenAtUtc !== 'string')
    ) {
      throw new Error('invalid-device-status');
    }
    return {
      deviceId: item.deviceId,
      name: item.name,
      locationName: item.locationName,
      locationIsActive: item.locationIsActive,
      isActive: item.isActive,
      isConnected: item.isConnected,
      lastSeenAtUtc: item.lastSeenAtUtc,
    };
  });

  return {
    items,
    page: page as number,
    pageSize: pageSize as number,
    totalCount: totalCount as number,
  };
}

async function mutateDevice(path: string, body?: unknown): Promise<DeviceMutationResult> {
  let token: string;
  try {
    token = await getCsrfToken();
  } catch (error) {
    if (error instanceof ApiError) throw new DeviceStatusApiError(error.status);
    throw error;
  }
  const response = await fetch(path, {
    method: 'POST',
    cache: 'no-store',
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      'X-CSRF-TOKEN': token,
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  if (!response.ok) throw new DeviceStatusApiError(response.status);
  const result: unknown = await response.json();
  if (
    !isRecord(result) ||
    typeof result.deviceId !== 'string' ||
    typeof result.isActive !== 'boolean' ||
    (result.revokedAtUtc !== undefined && typeof result.revokedAtUtc !== 'string')
  ) {
    throw new Error('invalid-device-mutation-response');
  }
  return {
    deviceId: result.deviceId,
    isActive: result.isActive,
    ...(typeof result.revokedAtUtc === 'string' ? { revokedAtUtc: result.revokedAtUtc } : {}),
  };
}

export function registerDevice(input: RegisterDeviceInput): Promise<DeviceMutationResult> {
  return mutateDevice('/api/v1/admin/devices', input);
}

export function revokeDevice(deviceId: string): Promise<DeviceMutationResult> {
  return mutateDevice(`/api/v1/admin/devices/${encodeURIComponent(deviceId)}/revoke`);
}

export async function getDeviceStatus(
  page: number,
  signal?: AbortSignal,
): Promise<DeviceStatusPage> {
  const query = new URLSearchParams({ page: String(page), pageSize: '25' });
  const response = await fetch(`/api/v1/admin/devices?${query}`, {
    ...(signal ? { signal } : {}),
    cache: 'no-store',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) throw new DeviceStatusApiError(response.status);
  return parseDeviceStatusPage(await response.json());
}
