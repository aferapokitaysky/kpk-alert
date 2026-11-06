import { ApiError } from './api';

export type ResourceState<T> =
  | { status: 'loading' }
  | { status: 'ready'; data: T }
  | { status: 'empty' }
  | { status: 'unauthenticated' }
  | { status: 'forbidden' }
  | { status: 'error' };

export type SessionState =
  | { status: 'loading' }
  | {
      status: 'authenticated';
      roles: string[];
      capabilities: { canOperate: boolean; canViewAudit: boolean };
    }
  | { status: 'unauthenticated' }
  | { status: 'forbidden' }
  | { status: 'error' };
export type ReadinessState = 'available' | 'unavailable' | 'unknown';

export function resourceFromResult<T>(
  result: PromiseSettledResult<T>,
  isEmpty: (value: T) => boolean,
): ResourceState<T> {
  if (result.status === 'fulfilled') {
    return isEmpty(result.value) ? { status: 'empty' } : { status: 'ready', data: result.value };
  }
  if (result.reason instanceof ApiError) {
    if (result.reason.status === 401) return { status: 'unauthenticated' };
    if (result.reason.status === 403) return { status: 'forbidden' };
  }
  return { status: 'error' };
}

export function readinessFromResponse(
  httpStatus: number,
  payloadStatus: string | null,
): ReadinessState {
  if (httpStatus >= 200 && httpStatus < 300 && payloadStatus === 'ok') return 'available';
  if (httpStatus === 503 || httpStatus >= 500) return 'unavailable';
  return 'unknown';
}

export function pageCount(totalCount: number, pageSize: number): number {
  return Math.max(1, Math.ceil(totalCount / pageSize));
}

export function commandErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 400:
        return 'Перевірте сценарій і вибрані корпуси.';
      case 401:
        return 'Сесію завершено. Увійдіть знову, щоб продовжити.';
      case 403:
        return 'У вас немає права запускати сценарій для цих корпусів.';
      case 409:
        return 'Запит із цим ключем уже має інші параметри. Перевірте вибір і повторіть дію.';
      case 422:
        return 'Сценарій або один із корпусів більше недоступний. Оновіть дані.';
      default:
        return 'Не вдалося зберегти команду. Перевірте з’єднання та повторіть спробу.';
    }
  }
  return 'Не вдалося підтвердити результат запиту. Повторіть дію з тими самими параметрами.';
}

export function stopCommandErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 400:
        return 'Не вдалося перевірити команду відбою. Оновіть сторінку та повторіть дію.';
      case 401:
        return 'Сесію завершено. Увійдіть знову, щоб продовжити.';
      case 403:
        return 'У вас немає права зупинити сигнал у вибраних корпусах.';
      case 409:
        return 'Сигнал уже завершився або ще не надійшов до всіх вузлів.';
      case 422:
        return 'Команда більше не доступна для зупинки. Оновіть дані.';
      default:
        return 'Не вдалося надіслати відбій. Перевірте з’єднання та повторіть спробу.';
    }
  }
  return 'Не вдалося підтвердити результат запиту. Повторіть відбій із тими самими параметрами.';
}
