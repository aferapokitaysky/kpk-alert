export type CommandDeliveryState =
  | 'Pending'
  | 'InFlight'
  | 'Accepted'
  | 'Playing'
  | 'Completed'
  | 'Stopped'
  | 'Rejected'
  | 'Failed'
  | 'Expired';

export interface CommandDelivery {
  deviceId: string;
  deviceName: string;
  state: CommandDeliveryState;
  createdAtUtc: string;
  expiresAtUtc: string;
  lastAttemptAtUtc: string | null;
  attemptCount: number;
}

export interface CommandDeliveryStateSummary {
  pending: number;
  inFlight: number;
  accepted: number;
  playing: number;
  completed: number;
  stopped: number;
  rejected: number;
  failed: number;
  expired: number;
}

export interface CommandDeliveriesPage {
  items: CommandDelivery[];
  page: number;
  pageSize: number;
  totalCount: number;
  hasUnfinishedDeliveries: boolean;
  stateSummary: CommandDeliveryStateSummary;
}

export class CommandDeliveriesApiError extends Error {
  constructor(readonly status: number) {
    super(`Command deliveries request failed with status ${status}`);
    this.name = 'CommandDeliveriesApiError';
  }
}

export const COMMAND_DELIVERIES_PAGE_SIZE = 25;

const deliveryStates: readonly CommandDeliveryState[] = [
  'Pending',
  'InFlight',
  'Accepted',
  'Playing',
  'Completed',
  'Stopped',
  'Rejected',
  'Failed',
  'Expired',
];

function parseStateSummary(value: unknown): CommandDeliveryStateSummary | null {
  if (
    !isRecord(value) ||
    Object.keys(value).length !== deliveryStates.length ||
    !isNonNegativeSafeInteger(value.pending) ||
    !isNonNegativeSafeInteger(value.inFlight) ||
    !isNonNegativeSafeInteger(value.accepted) ||
    !isNonNegativeSafeInteger(value.playing) ||
    !isNonNegativeSafeInteger(value.completed) ||
    !isNonNegativeSafeInteger(value.stopped) ||
    !isNonNegativeSafeInteger(value.rejected) ||
    !isNonNegativeSafeInteger(value.failed) ||
    !isNonNegativeSafeInteger(value.expired)
  ) {
    return null;
  }

  return {
    pending: value.pending,
    inFlight: value.inFlight,
    accepted: value.accepted,
    playing: value.playing,
    completed: value.completed,
    stopped: value.stopped,
    rejected: value.rejected,
    failed: value.failed,
    expired: value.expired,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isDate(value: unknown): value is string {
  return typeof value === 'string' && !Number.isNaN(Date.parse(value));
}

function isNullableDate(value: unknown): value is string | null {
  return value === null || isDate(value);
}

function isPositiveSafeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
}

function isNonNegativeSafeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

function isDeliveryState(value: unknown): value is CommandDeliveryState {
  return deliveryStates.some((state) => state === value);
}

function parseDelivery(value: unknown): CommandDelivery | null {
  if (
    !isRecord(value) ||
    typeof value.deviceId !== 'string' ||
    value.deviceId.length === 0 ||
    typeof value.deviceName !== 'string' ||
    value.deviceName.length === 0 ||
    !isDeliveryState(value.state) ||
    !isDate(value.createdAtUtc) ||
    !isDate(value.expiresAtUtc) ||
    !isNullableDate(value.lastAttemptAtUtc) ||
    !isNonNegativeSafeInteger(value.attemptCount)
  ) {
    return null;
  }

  return {
    deviceId: value.deviceId,
    deviceName: value.deviceName,
    state: value.state,
    createdAtUtc: value.createdAtUtc,
    expiresAtUtc: value.expiresAtUtc,
    lastAttemptAtUtc: value.lastAttemptAtUtc,
    attemptCount: value.attemptCount,
  };
}

export async function getCommandDeliveries(
  commandId: string,
  page: number,
  signal?: AbortSignal,
): Promise<CommandDeliveriesPage> {
  const pageSize = COMMAND_DELIVERIES_PAGE_SIZE;
  const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  const response = await fetch(
    `/api/v1/alert-commands/${encodeURIComponent(commandId)}/deliveries?${query}`,
    {
      method: 'GET',
      cache: 'no-store',
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
      ...(signal ? { signal } : {}),
    },
  );
  if (!response.ok) throw new CommandDeliveriesApiError(response.status);

  const value: unknown = await response.json();
  const stateSummary = isRecord(value) ? parseStateSummary(value.stateSummary) : null;
  if (
    !isRecord(value) ||
    !Array.isArray(value.items) ||
    !isPositiveSafeInteger(value.page) ||
    value.page !== page ||
    !isPositiveSafeInteger(value.pageSize) ||
    value.pageSize !== pageSize ||
    !isNonNegativeSafeInteger(value.totalCount) ||
    typeof value.hasUnfinishedDeliveries !== 'boolean' ||
    stateSummary === null ||
    Object.values(stateSummary).reduce((sum, count) => sum + count, 0) !== value.totalCount
  ) {
    throw new Error('invalid-command-deliveries-response');
  }

  const items: CommandDelivery[] = [];
  for (const item of value.items) {
    const delivery = parseDelivery(item);
    if (!delivery) throw new Error('invalid-command-deliveries-response');
    items.push(delivery);
  }
  if (items.length > pageSize) {
    throw new Error('invalid-command-deliveries-response');
  }

  return {
    items,
    page: value.page,
    pageSize: value.pageSize,
    totalCount: value.totalCount,
    hasUnfinishedDeliveries: value.hasUnfinishedDeliveries,
    stateSummary,
  };
}
