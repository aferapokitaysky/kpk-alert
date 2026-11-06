import { ApiError, getCsrfToken } from '../../api';

export interface ManagedUserRole {
  assignmentId: string;
  roleCode: string;
  roleName: string;
  locationId: string | null;
  locationName: string | null;
}

export interface ManagedUser {
  id: string;
  email: string;
  isActive: boolean;
  roles: ManagedUserRole[];
}

export interface ManagedUserPage {
  items: ManagedUser[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ManagedRoleCatalogEntry {
  code: string;
  name: string;
  globalOnly: boolean;
  supportsLocationScope: boolean;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parseRole(value: unknown): ManagedUserRole {
  if (
    !isRecord(value) ||
    typeof value.assignmentId !== 'string' ||
    typeof value.roleCode !== 'string' ||
    typeof value.roleName !== 'string' ||
    (typeof value.locationId !== 'string' && value.locationId !== null) ||
    (typeof value.locationName !== 'string' && value.locationName !== null)
  ) {
    throw new Error('invalid-user-role-response');
  }

  return {
    assignmentId: value.assignmentId,
    roleCode: value.roleCode,
    roleName: value.roleName,
    locationId: value.locationId,
    locationName: value.locationName,
  };
}

function parseUser(value: unknown): ManagedUser {
  if (
    !isRecord(value) ||
    typeof value.id !== 'string' ||
    typeof value.email !== 'string' ||
    typeof value.isActive !== 'boolean' ||
    !Array.isArray(value.roles)
  ) {
    throw new Error('invalid-user-response');
  }

  return {
    id: value.id,
    email: value.email,
    isActive: value.isActive,
    roles: value.roles.map(parseRole),
  };
}

async function readResponse(response: Response): Promise<unknown> {
  if (!response.ok) throw new ApiError(response.status);
  return response.status === 204 ? null : response.json();
}

export async function getManagedUsers(
  page: number,
  search: string,
  signal?: AbortSignal,
): Promise<ManagedUserPage> {
  const query = new URLSearchParams({ page: String(page), pageSize: '25' });
  if (search.trim()) query.set('search', search.trim());
  const response = await fetch(`/api/v1/admin/users?${query}`, {
    cache: 'no-store',
    credentials: 'same-origin',
    ...(signal ? { signal } : {}),
    headers: { Accept: 'application/json' },
  });
  const body: unknown = await readResponse(response);
  if (
    !isRecord(body) ||
    !Array.isArray(body.items) ||
    typeof body.page !== 'number' ||
    typeof body.pageSize !== 'number' ||
    typeof body.totalCount !== 'number'
  ) {
    throw new Error('invalid-users-response');
  }

  return {
    items: body.items.map(parseUser),
    page: body.page,
    pageSize: body.pageSize,
    totalCount: body.totalCount,
  };
}

export async function getManagedRoleCatalog(
  signal?: AbortSignal,
): Promise<ManagedRoleCatalogEntry[]> {
  const response = await fetch('/api/v1/admin/users/role-catalog', {
    cache: 'no-store',
    credentials: 'same-origin',
    ...(signal ? { signal } : {}),
    headers: { Accept: 'application/json' },
  });
  const body: unknown = await readResponse(response);
  if (!Array.isArray(body)) throw new Error('invalid-role-catalog-response');

  return body.map((role): ManagedRoleCatalogEntry => {
    if (
      !isRecord(role) ||
      typeof role.code !== 'string' ||
      typeof role.name !== 'string' ||
      typeof role.globalOnly !== 'boolean' ||
      typeof role.supportsLocationScope !== 'boolean'
    ) {
      throw new Error('invalid-role-catalog-response');
    }
    return {
      code: role.code,
      name: role.name,
      globalOnly: role.globalOnly,
      supportsLocationScope: role.supportsLocationScope,
    };
  });
}

async function sendMutation(path: string, method: string, body?: unknown): Promise<void> {
  const token = await getCsrfToken();
  const response = await fetch(path, {
    method,
    cache: 'no-store',
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      'X-CSRF-TOKEN': token,
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  await readResponse(response);
}

export function updateManagedUserStatus(userId: string, isActive: boolean): Promise<void> {
  return sendMutation(`/api/v1/admin/users/${encodeURIComponent(userId)}/status`, 'PATCH', {
    isActive,
  });
}

export function grantManagedUserRole(
  userId: string,
  roleCode: string,
  locationId: string | null,
): Promise<void> {
  return sendMutation(`/api/v1/admin/users/${encodeURIComponent(userId)}/roles`, 'POST', {
    roleCode,
    locationId,
  });
}

export function revokeManagedUserRole(userId: string, assignmentId: string): Promise<void> {
  return sendMutation(
    `/api/v1/admin/users/${encodeURIComponent(userId)}/roles/${encodeURIComponent(assignmentId)}`,
    'DELETE',
  );
}
