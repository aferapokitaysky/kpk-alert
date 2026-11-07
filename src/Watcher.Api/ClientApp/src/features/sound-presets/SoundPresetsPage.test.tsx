import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../api';
import { SoundPresetsPage } from './SoundPresetsPage';
import {
  createSoundPreset,
  getSoundPresets,
  setSoundPresetPublication,
  updateSoundPreset,
} from './api';

vi.mock('./api', () => ({
  createSoundPreset: vi.fn(),
  getSoundPresets: vi.fn(),
  setSoundPresetPublication: vi.fn(),
  updateSoundPreset: vi.fn(),
}));

const api = {
  create: vi.mocked(createSoundPreset),
  get: vi.mocked(getSoundPresets),
  publication: vi.mocked(setSoundPresetPublication),
  update: vi.mocked(updateSoundPreset),
};
const adminSession = {
  status: 'authenticated' as const,
  roles: ['admin'],
  capabilities: { canOperate: true, canViewAudit: true },
};
const preset = {
  code: 'air_alert',
  name: 'Повітряна тривога',
  priority: 10,
  manifestHash: 'a'.repeat(64),
  isPublished: false,
};

afterEach(() => {
  vi.clearAllMocks();
});

describe('керування звуковими сценаріями', () => {
  it('не запитує список і не показує форму користувачу без ролі адміністратора', () => {
    const onSessionExpired = vi.fn();
    render(
      <SoundPresetsPage
        session={{
          status: 'authenticated',
          roles: ['auditor'],
          capabilities: { canOperate: false, canViewAudit: true },
        }}
        onSessionExpired={onSessionExpired}
      />,
    );
    expect(screen.getByRole('status')).toHaveTextContent('лише глобальному адміністратору');
    expect(screen.queryByRole('button', { name: 'Створити чернетку' })).not.toBeInTheDocument();
    expect(api.get).not.toHaveBeenCalled();
  });

  it('показує опубліковані та чернеткові сценарії з окремими доступними діями', async () => {
    api.get.mockResolvedValue([
      preset,
      { ...preset, code: 'minute_silence', name: 'Хвилина мовчання', isPublished: true },
    ]);
    render(<SoundPresetsPage session={adminSession} onSessionExpired={vi.fn()} />);
    expect(await screen.findByText('Повітряна тривога')).toBeInTheDocument();
    expect(screen.getByText('Хвилина мовчання')).toBeInTheDocument();
    const draftItem = screen.getByText('Повітряна тривога').closest('li');
    const publishedItem = screen.getByText('Хвилина мовчання').closest('li');
    expect(draftItem).not.toBeNull();
    expect(publishedItem).not.toBeNull();
    expect(
      within(draftItem as HTMLElement).getByRole('button', { name: 'Редагувати' }),
    ).toBeInTheDocument();
    expect(
      within(publishedItem as HTMLElement).queryByRole('button', { name: 'Редагувати' }),
    ).not.toBeInTheDocument();
    expect(
      within(publishedItem as HTMLElement).getByRole('button', { name: 'Зняти публікацію' }),
    ).toBeInTheDocument();
  });

  it('створює чернетку із валідованими полями та оновлює список', async () => {
    api.get.mockResolvedValueOnce([]).mockResolvedValueOnce([preset]);
    api.create.mockResolvedValue(preset);
    render(<SoundPresetsPage session={adminSession} onSessionExpired={vi.fn()} />);
    await screen.findByText('Сценаріїв поки немає. Створіть перший.');
    fireEvent.change(screen.getByRole('textbox', { name: /Код сценарію/ }), {
      target: { value: 'air_alert' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: 'Назва' }), {
      target: { value: 'Повітряна тривога' },
    });
    fireEvent.change(screen.getByRole('spinbutton', { name: /Пріоритет/ }), {
      target: { value: '10' },
    });
    fireEvent.change(screen.getByRole('textbox', { name: /SHA-256 маніфесту/ }), {
      target: { value: 'a'.repeat(64) },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Створити чернетку' }));

    await waitFor(() =>
      expect(api.create).toHaveBeenCalledWith({
        code: 'air_alert',
        name: 'Повітряна тривога',
        priority: 10,
        manifestHash: 'a'.repeat(64),
      }),
    );
    expect(await screen.findByText('Сценарій створено як чернетку.')).toBeInTheDocument();
    expect(api.get).toHaveBeenCalledTimes(2);
  });

  it('знімає публікацію та пояснює конфлікт сервера', async () => {
    api.get.mockResolvedValue([{ ...preset, isPublished: true }]);
    api.publication.mockRejectedValue(new ApiError(409));
    render(<SoundPresetsPage session={adminSession} onSessionExpired={vi.fn()} />);
    const unpublish = await screen.findByRole('button', { name: 'Зняти публікацію' });
    fireEvent.click(unpublish);
    await waitFor(() => expect(api.publication).toHaveBeenCalledWith('air_alert', false));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Сценарій змінився або його опубліковано',
    );
  });

  it('передає назву, пріоритет і маніфест лише для чернетки', async () => {
    api.get.mockResolvedValue([preset]);
    api.update.mockResolvedValue({ ...preset, name: 'Оновлена назва' });
    render(<SoundPresetsPage session={adminSession} onSessionExpired={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Редагувати' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Назва' }), {
      target: { value: 'Оновлена назва' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Зберегти зміни' }));
    await waitFor(() =>
      expect(api.update).toHaveBeenCalledWith({
        code: preset.code,
        name: 'Оновлена назва',
        priority: preset.priority,
        manifestHash: preset.manifestHash,
      }),
    );
  });

  it('повідомляє про завершення сесії після відповіді 401', async () => {
    api.get.mockRejectedValue(new ApiError(401));
    const onSessionExpired = vi.fn();
    render(<SoundPresetsPage session={adminSession} onSessionExpired={onSessionExpired} />);
    await waitFor(() => expect(onSessionExpired).toHaveBeenCalledOnce());
    expect(await screen.findByText('Сесію завершено. Увійдіть знову.')).toBeInTheDocument();
  });
});
