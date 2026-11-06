export interface DeviceStatus {
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
