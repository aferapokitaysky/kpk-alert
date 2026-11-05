export type MinuteOfSilenceOutcome = 'queued' | 'skipped' | null;

export interface MinuteOfSilenceStatus {
  runDate: string;
  timeZone: 'Europe/Kyiv';
  scheduledAtUtc: string;
  outcome: MinuteOfSilenceOutcome;
  skipReason: string | null;
  commandId: string | null;
  processedAtUtc: string | null;
}

export class MinuteOfSilenceApiError extends Error {
  constructor(readonly status: number) {
    super(`Minute of silence status request failed with status ${status}`);
    this.name = 'MinuteOfSilenceApiError';
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isDateTime(value: unknown): value is string {
  return typeof value === 'string' && !Number.isNaN(Date.parse(value));
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function isValidRunDate(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const parsedDate = new Date(`${value}T00:00:00.000Z`);
  return !Number.isNaN(parsedDate.getTime()) && parsedDate.toISOString().slice(0, 10) === value;
}

function parseMinuteOfSilenceStatus(value: unknown): MinuteOfSilenceStatus | null {
  if (
    !isRecord(value) ||
    !isValidRunDate(value.runDate) ||
    value.timeZone !== 'Europe/Kyiv' ||
    !isDateTime(value.scheduledAtUtc) ||
    (value.outcome !== null && value.outcome !== 'queued' && value.outcome !== 'skipped') ||
    !isNullableString(value.skipReason) ||
    !isNullableString(value.commandId) ||
    (value.processedAtUtc !== null && !isDateTime(value.processedAtUtc))
  ) {
    return null;
  }

  return {
    runDate: value.runDate,
    timeZone: value.timeZone,
    scheduledAtUtc: value.scheduledAtUtc,
    outcome: value.outcome,
    skipReason: value.skipReason,
    commandId: value.commandId,
    processedAtUtc: value.processedAtUtc,
  };
}

export async function getMinuteOfSilenceStatus(
  signal?: AbortSignal,
): Promise<MinuteOfSilenceStatus> {
  const response = await fetch('/api/v1/minute-of-silence/status/today', {
    method: 'GET',
    cache: 'no-store',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
    ...(signal ? { signal } : {}),
  });
  if (!response.ok) throw new MinuteOfSilenceApiError(response.status);

  const status = parseMinuteOfSilenceStatus(await response.json());
  if (status === null) throw new Error('invalid-minute-of-silence-status-response');
  return status;
}
