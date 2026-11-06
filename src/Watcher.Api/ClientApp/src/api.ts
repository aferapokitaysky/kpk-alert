export interface Location {
  id: string;
  name: string;
  code: string;
}

export interface OperatorSession {
  authenticated: true;
  roles: string[];
  capabilities: {
    canOperate: boolean;
    canViewAudit: boolean;
  };
}

export interface Preset {
  code: string;
  name: string;
}

export interface CommandTarget {
  locationId: string;
  state: string;
}

export interface AlertCommand {
  id: string;
  action: 'start' | 'stop';
  presetCode: string | null;
  targetCommandId: string | null;
  createdAtUtc: string;
  targets: CommandTarget[];
}

export interface AuditEvent {
  id: string;
  eventType: string;
  actorType: string;
  actorId: string | null;
  occurredAtUtc: string;
  payload: unknown;
}

export interface AuditPage {
  items: AuditEvent[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface AuditEventFilters {
  actorId?: string;
  fromUtc?: string;
  toUtc?: string;
}

export class ApiError extends Error {
  constructor(readonly status: number) {
    super(`API request failed with status ${status}`);
    this.name = 'ApiError';
  }
}

async function request(path: string, init: RequestInit = {}): Promise<Response> {
  return fetch(path, {
    ...init,
    cache: 'no-store',
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      ...init.headers,
    },
  });
}

async function getJson(path: string): Promise<unknown> {
  const response = await request(path);
  if (!response.ok) throw new ApiError(response.status);
  return response.json() as Promise<unknown>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export async function getLocations(): Promise<Location[]> {
  const result = await getJson('/api/v1/locations');
  if (!Array.isArray(result) || !result.every(isRecord))
    throw new Error('invalid-locations-response');
  return result.map((location) => {
    if (
      typeof location.id !== 'string' ||
      typeof location.name !== 'string' ||
      typeof location.code !== 'string'
    ) {
      throw new Error('invalid-locations-response');
    }
    return { id: location.id, name: location.name, code: location.code };
  });
}

export async function getPresets(): Promise<Preset[]> {
  const result = await getJson('/api/v1/presets');
  if (!Array.isArray(result) || !result.every(isRecord))
    throw new Error('invalid-presets-response');
  return result.map((preset) => {
    if (typeof preset.code !== 'string' || typeof preset.name !== 'string') {
      throw new Error('invalid-presets-response');
    }
    return { code: preset.code, name: preset.name };
  });
}

export async function getSession(): Promise<OperatorSession> {
  const result = await getJson('/api/v1/auth/session');
  if (
    !isRecord(result) ||
    result.authenticated !== true ||
    !Array.isArray(result.roles) ||
    !result.roles.every((role) => typeof role === 'string') ||
    !isRecord(result.capabilities) ||
    typeof result.capabilities.canOperate !== 'boolean' ||
    typeof result.capabilities.canViewAudit !== 'boolean'
  ) {
    throw new Error('invalid-session-response');
  }
  return {
    authenticated: true,
    roles: result.roles,
    capabilities: {
      canOperate: result.capabilities.canOperate,
      canViewAudit: result.capabilities.canViewAudit,
    },
  };
}

export async function getReadiness(): Promise<{ httpStatus: number; status: string | null }> {
  const response = await request('/api/v1/health/ready');
  let payload: unknown = null;
  if (response.ok) payload = await response.json();
  const status = isRecord(payload) && typeof payload.status === 'string' ? payload.status : null;
  return { httpStatus: response.status, status };
}

export async function getCsrfToken(): Promise<string> {
  const result = await getJson('/api/v1/auth/csrf-token');
  if (!isRecord(result) || typeof result.token !== 'string' || result.token.length === 0) {
    throw new Error('csrf-token-unavailable');
  }
  return result.token;
}

export async function logout(): Promise<void> {
  const token = await getCsrfToken();
  const response = await request('/api/v1/auth/logout', {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': token },
  });
  if (!response.ok) throw new ApiError(response.status);
}

export async function createAlertCommand(
  presetCode: string,
  locationIds: string[],
  idempotencyKey: string,
): Promise<AlertCommand> {
  const token = await getCsrfToken();
  const response = await request('/api/v1/alert-commands', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': token,
      'Idempotency-Key': idempotencyKey,
    },
    body: JSON.stringify({ action: 'start', presetCode, locationIds }),
  });
  if (response.status !== 202) throw new ApiError(response.status);

  const body: unknown = await response.json();
  if (
    !isRecord(body) ||
    typeof body.id !== 'string' ||
    body.action !== 'start' ||
    typeof body.presetCode !== 'string' ||
    typeof body.createdAtUtc !== 'string' ||
    !Array.isArray(body.targets) ||
    !body.targets.every(isRecord)
  ) {
    throw new Error('invalid-command-response');
  }

  const targets = body.targets.map((target) => {
    if (typeof target.locationId !== 'string' || typeof target.state !== 'string') {
      throw new Error('invalid-command-response');
    }
    return { locationId: target.locationId, state: target.state };
  });

  return {
    id: body.id,
    action: 'start',
    presetCode: body.presetCode,
    targetCommandId: typeof body.targetCommandId === 'string' ? body.targetCommandId : null,
    createdAtUtc: body.createdAtUtc,
    targets,
  };
}

export async function createStopCommand(
  targetCommandId: string,
  locationIds: string[],
  idempotencyKey: string,
): Promise<AlertCommand> {
  if (!targetCommandId || locationIds.length === 0) throw new Error('invalid-stop-command');
  const token = await getCsrfToken();
  const response = await request(
    `/api/v1/alert-commands/${encodeURIComponent(targetCommandId)}/stop`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': token,
        'Idempotency-Key': idempotencyKey,
      },
      body: JSON.stringify({ locationIds }),
    },
  );
  if (response.status !== 202) throw new ApiError(response.status);

  const body: unknown = await response.json();
  if (
    !isRecord(body) ||
    typeof body.id !== 'string' ||
    body.action !== 'stop' ||
    body.presetCode !== null ||
    body.targetCommandId !== targetCommandId ||
    typeof body.createdAtUtc !== 'string' ||
    !Array.isArray(body.targets) ||
    !body.targets.every(isRecord)
  ) {
    throw new Error('invalid-command-response');
  }

  const targets = body.targets.map((target) => {
    if (typeof target.locationId !== 'string' || typeof target.state !== 'string') {
      throw new Error('invalid-command-response');
    }
    return { locationId: target.locationId, state: target.state };
  });

  return {
    id: body.id,
    action: 'stop',
    presetCode: null,
    targetCommandId,
    createdAtUtc: body.createdAtUtc,
    targets,
  };
}

export async function getAlertCommand(commandId: string): Promise<AlertCommand> {
  const result = await getJson(`/api/v1/alert-commands/${encodeURIComponent(commandId)}`);
  if (
    !isRecord(result) ||
    typeof result.id !== 'string' ||
    result.id !== commandId ||
    typeof result.createdAtUtc !== 'string' ||
    !Array.isArray(result.targets) ||
    !result.targets.every(isRecord)
  ) {
    throw new Error('invalid-command-response');
  }

  let action: AlertCommand['action'];
  let presetCode: string | null;
  let targetCommandId: string | null;
  if (
    result.action === 'start' &&
    typeof result.presetCode === 'string' &&
    result.targetCommandId === null
  ) {
    action = result.action;
    presetCode = result.presetCode;
    targetCommandId = null;
  } else if (
    result.action === 'stop' &&
    result.presetCode === null &&
    typeof result.targetCommandId === 'string'
  ) {
    action = result.action;
    presetCode = null;
    targetCommandId = result.targetCommandId;
  } else {
    throw new Error('invalid-command-response');
  }

  const targets = result.targets.map((target) => {
    if (typeof target.locationId !== 'string' || typeof target.state !== 'string') {
      throw new Error('invalid-command-response');
    }
    return { locationId: target.locationId, state: target.state };
  });

  return {
    id: result.id,
    action,
    presetCode,
    targetCommandId,
    createdAtUtc: result.createdAtUtc,
    targets,
  };
}

export async function getAuditEvents(
  page: number,
  pageSize: number,
  eventType: string,
  filters: AuditEventFilters = {},
): Promise<AuditPage> {
  const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  if (eventType.length > 0) query.set('eventType', eventType);
  if (filters.actorId) query.set('actorId', filters.actorId);
  if (filters.fromUtc) query.set('fromUtc', filters.fromUtc);
  if (filters.toUtc) query.set('toUtc', filters.toUtc);
  const result = await getJson(`/api/v1/audit-events?${query.toString()}`);
  if (
    !isRecord(result) ||
    !Array.isArray(result.items) ||
    !result.items.every(isRecord) ||
    typeof result.page !== 'number' ||
    typeof result.pageSize !== 'number' ||
    typeof result.totalCount !== 'number'
  ) {
    throw new Error('invalid-audit-response');
  }

  const items = result.items.map((item): AuditEvent => {
    if (
      typeof item.id !== 'string' ||
      typeof item.eventType !== 'string' ||
      typeof item.actorType !== 'string' ||
      (typeof item.actorId !== 'string' && item.actorId !== null) ||
      typeof item.occurredAtUtc !== 'string' ||
      !('payload' in item)
    ) {
      throw new Error('invalid-audit-response');
    }
    return {
      id: item.id,
      eventType: item.eventType,
      actorType: item.actorType,
      actorId: item.actorId,
      occurredAtUtc: item.occurredAtUtc,
      payload: item.payload,
    };
  });

  return {
    items,
    page: result.page,
    pageSize: result.pageSize,
    totalCount: result.totalCount,
  };
}
