export type AlertLevel = 'active' | 'inactive' | 'partial' | 'unknown' | 'conflict';
export type SourceFreshness = 'fresh' | 'stale' | 'unknown';
export type AlertScope = 'oblast' | 'raion' | 'hromada';

export interface AlertSourceObservation {
  source: string;
  sourceName: string;
  scope: AlertScope;
  scopeId: string;
  scopeName: string;
  level: AlertLevel;
  alertLevel: 'red' | 'yellow' | null;
  freshness: SourceFreshness;
  checkedAtUtc: string | null;
  dataUpdatedAtUtc: string | null;
  errorCode: string | null;
  attributionUrl: string;
  lastKnownLevel: AlertLevel | null;
  lastKnownAlertLevel: 'red' | 'yellow' | null;
}

export interface AlertTargetStatus {
  scope: AlertScope;
  scopeId: string;
  scopeName: string;
  level: AlertLevel;
  alertLevel: 'red' | 'yellow' | null;
  freshness: SourceFreshness;
  checkedAtUtc: string | null;
  dataUpdatedAtUtc: string | null;
  authoritativeObservation: AlertSourceObservation | null;
  observations: AlertSourceObservation[];
}

export interface AlertSourcesStatus {
  checkedAtUtc: string;
  targets: AlertTargetStatus[];
}

export class AlertSourceApiError extends Error {
  constructor(readonly status: number) {
    super(`Alert source request failed with status ${status}`);
    this.name = 'AlertSourceApiError';
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isAlertLevel(value: unknown): value is AlertLevel {
  return ['active', 'inactive', 'partial', 'unknown', 'conflict'].includes(String(value));
}

function isFreshness(value: unknown): value is SourceFreshness {
  return ['fresh', 'stale', 'unknown'].includes(String(value));
}

function isScope(value: unknown): value is AlertScope {
  return value === 'oblast' || value === 'raion' || value === 'hromada';
}

function isDate(value: unknown): value is string | null {
  return value === null || (typeof value === 'string' && !Number.isNaN(Date.parse(value)));
}

function isAlertSeverity(value: unknown): value is 'red' | 'yellow' | null {
  return value === null || value === 'red' || value === 'yellow';
}

function isHttpsUrl(value: unknown): value is string {
  if (typeof value !== 'string') return false;
  try {
    return new URL(value).protocol === 'https:';
  } catch {
    return false;
  }
}

function parseObservation(value: unknown): AlertSourceObservation | null {
  if (
    !isRecord(value) ||
    typeof value.source !== 'string' ||
    typeof value.sourceName !== 'string' ||
    !isScope(value.scope) ||
    typeof value.scopeId !== 'string' ||
    typeof value.scopeName !== 'string' ||
    !isAlertLevel(value.level) ||
    !isAlertSeverity(value.alertLevel) ||
    !isFreshness(value.freshness) ||
    !isDate(value.checkedAtUtc) ||
    !isDate(value.dataUpdatedAtUtc) ||
    (value.errorCode !== null && typeof value.errorCode !== 'string') ||
    !isHttpsUrl(value.attributionUrl) ||
    (value.lastKnownLevel !== null && !isAlertLevel(value.lastKnownLevel)) ||
    (value.lastKnownAlertLevel !== null && !isAlertSeverity(value.lastKnownAlertLevel))
  ) {
    return null;
  }

  return value as unknown as AlertSourceObservation;
}

function parseTarget(value: unknown): AlertTargetStatus | null {
  if (
    !isRecord(value) ||
    !isScope(value.scope) ||
    typeof value.scopeId !== 'string' ||
    typeof value.scopeName !== 'string' ||
    !isAlertLevel(value.level) ||
    !isAlertSeverity(value.alertLevel) ||
    !isFreshness(value.freshness) ||
    !isDate(value.checkedAtUtc) ||
    !isDate(value.dataUpdatedAtUtc) ||
    !Array.isArray(value.observations)
  ) {
    return null;
  }

  const observations = value.observations.map(parseObservation);
  const authoritativeObservation =
    value.authoritativeObservation === null
      ? null
      : parseObservation(value.authoritativeObservation);
  if (
    observations.some((observation) => observation === null) ||
    (value.authoritativeObservation !== null && authoritativeObservation === null)
  ) {
    return null;
  }

  return {
    scope: value.scope,
    scopeId: value.scopeId,
    scopeName: value.scopeName,
    level: value.level,
    alertLevel: value.alertLevel,
    freshness: value.freshness,
    checkedAtUtc: value.checkedAtUtc,
    dataUpdatedAtUtc: value.dataUpdatedAtUtc,
    authoritativeObservation,
    observations: observations as AlertSourceObservation[],
  };
}

export async function getAlertSourcesStatus(signal?: AbortSignal): Promise<AlertSourcesStatus> {
  const response = await fetch('/api/v1/alert-sources/status', {
    method: 'GET',
    cache: 'no-store',
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
    ...(signal ? { signal } : {}),
  });
  if (!response.ok) throw new AlertSourceApiError(response.status);

  const value: unknown = await response.json();
  if (
    !isRecord(value) ||
    !isDate(value.checkedAtUtc) ||
    value.checkedAtUtc === null ||
    !Array.isArray(value.targets)
  ) {
    throw new Error('invalid-alert-source-response');
  }

  const targets = value.targets.map(parseTarget);
  if (targets.some((target) => target === null)) {
    throw new Error('invalid-alert-source-response');
  }

  return { checkedAtUtc: value.checkedAtUtc, targets: targets as AlertTargetStatus[] };
}
