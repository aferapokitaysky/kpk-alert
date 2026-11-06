import { getCsrfToken } from '../../api';

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

export type UkraineAlarmWebhookState = 'registered' | 'not_registered' | 'unknown';
export type UkraineAlarmWebhookAction = 'register' | 'update';
export type UkraineAlarmWebhookOperationState =
  'Pending' | 'Running' | 'Succeeded' | 'Rejected' | 'Unknown' | 'Resolved';

export interface UkraineAlarmWebhookOperation {
  id: string;
  action: UkraineAlarmWebhookAction;
  state: UkraineAlarmWebhookOperationState;
  callbackUrl: string;
  outcomeCode: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface UkraineAlarmWebhookRegistrationStatus {
  integrationCode: 'ukrainealarm_webhook';
  state: UkraineAlarmWebhookState;
  updatedAtUtc: string;
  activeOperation: UkraineAlarmWebhookOperation | null;
}

export class UkraineAlarmWebhookApiError extends Error {
  constructor(readonly status: number) {
    super(`UkraineAlarm webhook request failed with status ${status}`);
    this.name = 'UkraineAlarmWebhookApiError';
  }
}

const webhookOperationsPath = '/api/v1/admin/integrations/ukrainealarm/webhook-operations';

function isWebhookDate(value: unknown): value is string {
  return typeof value === 'string' && !Number.isNaN(Date.parse(value));
}

function isWebhookOperationState(value: unknown): value is UkraineAlarmWebhookOperationState {
  return (
    value === 'Pending' ||
    value === 'Running' ||
    value === 'Succeeded' ||
    value === 'Rejected' ||
    value === 'Unknown' ||
    value === 'Resolved'
  );
}

function parseWebhookOperation(value: unknown): UkraineAlarmWebhookOperation | null {
  if (
    !isRecord(value) ||
    typeof value.id !== 'string' ||
    (value.action !== 'register' && value.action !== 'update') ||
    !isWebhookOperationState(value.state) ||
    typeof value.callbackUrl !== 'string' ||
    !isWebhookDate(value.createdAtUtc) ||
    !isWebhookDate(value.updatedAtUtc) ||
    (value.outcomeCode !== null && typeof value.outcomeCode !== 'string')
  ) {
    return null;
  }

  let callbackUrl: URL;
  try {
    callbackUrl = new URL(value.callbackUrl);
  } catch {
    return null;
  }
  if (callbackUrl.protocol !== 'https:') return null;

  return {
    id: value.id,
    action: value.action,
    state: value.state,
    callbackUrl: callbackUrl.href,
    outcomeCode: value.outcomeCode,
    createdAtUtc: value.createdAtUtc,
    updatedAtUtc: value.updatedAtUtc,
  };
}

async function readWebhookJson(
  path: string,
  init?: RequestInit,
  signal?: AbortSignal,
): Promise<unknown> {
  const response = await fetch(path, {
    ...init,
    cache: 'no-store',
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      ...init?.headers,
    },
    ...(signal ? { signal } : {}),
  });
  if (!response.ok) throw new UkraineAlarmWebhookApiError(response.status);
  return response.json() as Promise<unknown>;
}

export async function getUkraineAlarmWebhookStatus(
  signal?: AbortSignal,
): Promise<UkraineAlarmWebhookRegistrationStatus> {
  const value = await readWebhookJson(`${webhookOperationsPath}/state`, undefined, signal);
  return parseWebhookStatus(value);
}

export async function startUkraineAlarmWebhookOperation(
  action: UkraineAlarmWebhookAction,
  idempotencyKey: string,
): Promise<UkraineAlarmWebhookOperation> {
  const token = await getCsrfToken();
  const value = await readWebhookJson(webhookOperationsPath, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': token,
      'Idempotency-Key': idempotencyKey,
    },
    body: JSON.stringify({ action }),
  });
  const operation = parseWebhookOperation(value);
  if (!operation) throw new Error('invalid-ukrainealarm-webhook-operation');
  return operation;
}

export async function reconcileUkraineAlarmWebhookState(
  isRegistered: boolean,
): Promise<UkraineAlarmWebhookRegistrationStatus> {
  const token = await getCsrfToken();
  const value = await readWebhookJson(`${webhookOperationsPath}/state/reconcile`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': token,
    },
    body: JSON.stringify({ confirmed: true, isRegistered }),
  });
  return parseWebhookStatus(value);
}

export async function resolveUkraineAlarmWebhookOperation(
  operationId: string,
  isRegistered: boolean,
): Promise<UkraineAlarmWebhookOperation> {
  const token = await getCsrfToken();
  const value = await readWebhookJson(
    `${webhookOperationsPath}/${encodeURIComponent(operationId)}/resolve`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': token,
      },
      body: JSON.stringify({ confirmed: true, isRegistered }),
    },
  );
  const operation = parseWebhookOperation(value);
  if (!operation) throw new Error('invalid-ukrainealarm-webhook-operation');
  return operation;
}

function parseWebhookStatus(value: unknown): UkraineAlarmWebhookRegistrationStatus {
  if (
    !isRecord(value) ||
    value.integrationCode !== 'ukrainealarm_webhook' ||
    (value.state !== 'registered' &&
      value.state !== 'not_registered' &&
      value.state !== 'unknown') ||
    !isWebhookDate(value.updatedAtUtc)
  ) {
    throw new Error('invalid-ukrainealarm-webhook-state');
  }
  const activeOperation =
    value.activeOperation === null ? null : parseWebhookOperation(value.activeOperation);
  if (value.activeOperation !== null && activeOperation === null) {
    throw new Error('invalid-ukrainealarm-webhook-state');
  }
  return {
    integrationCode: value.integrationCode,
    state: value.state,
    updatedAtUtc: value.updatedAtUtc,
    activeOperation,
  };
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
