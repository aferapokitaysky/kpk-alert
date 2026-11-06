export interface MinuteOfSilenceHistoryEntry {
  runDate: string;
  outcome: 'queued' | 'skipped';
  skipReason: string | null;
  processedAtUtc: string;
}

export interface MinuteOfSilenceHistoryPage {
  items: MinuteOfSilenceHistoryEntry[];
  from: string;
  to: string;
  page: number;
  pageSize: number;
  totalCount: number;
}

export class MinuteOfSilenceHistoryApiError extends Error {
  constructor(readonly status: number) {
    super(`Minute of silence history request failed with status ${status}`);
    this.name = 'MinuteOfSilenceHistoryApiError';
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isValidRunDate(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const parsed = new Date(`${value}T00:00:00.000Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value;
}

function isDateTime(value: unknown): value is string {
  return typeof value === 'string' && !Number.isNaN(Date.parse(value));
}

function parseHistoryPage(value: unknown): MinuteOfSilenceHistoryPage | null {
  if (
    !isRecord(value) ||
    !Array.isArray(value.items) ||
    !isValidRunDate(value.from) ||
    !isValidRunDate(value.to) ||
    !Number.isInteger(value.page) ||
    !Number.isInteger(value.pageSize) ||
    !Number.isInteger(value.totalCount) ||
    (value.page as number) < 1 ||
    (value.pageSize as number) < 1 ||
    (value.totalCount as number) < 0 ||
    !value.items.every(
      (item) =>
        isRecord(item) &&
        isValidRunDate(item.runDate) &&
        (item.outcome === 'queued' || item.outcome === 'skipped') &&
        (item.skipReason === null || typeof item.skipReason === 'string') &&
        isDateTime(item.processedAtUtc),
    )
  ) {
    return null;
  }

  return value as unknown as MinuteOfSilenceHistoryPage;
}

export async function getMinuteOfSilenceHistory(
  from: string,
  to: string,
  page: number,
  pageSize: number,
  signal?: AbortSignal,
): Promise<MinuteOfSilenceHistoryPage> {
  const query = new URLSearchParams({ from, to, page: String(page), pageSize: String(pageSize) });
  const response = await fetch(`/api/v1/minute-of-silence/history?${query}`, {
    method: 'GET',
    cache: 'no-store',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
    ...(signal ? { signal } : {}),
  });
  if (!response.ok) throw new MinuteOfSilenceHistoryApiError(response.status);

  const result = parseHistoryPage(await response.json());
  if (result === null) throw new Error('invalid-minute-of-silence-history-response');
  return result;
}
