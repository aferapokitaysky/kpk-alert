import { useEffect, useRef } from 'react';
import type { Location, Preset } from '../../api';

interface Props {
  preset: Preset;
  locations: Location[];
  pending: boolean;
  error: string;
  onCancel: () => void;
  onConfirm: (locations: Location[]) => void;
}

export function CommandDialog({ preset, locations, pending, error, onCancel, onConfirm }: Props) {
  const dialogRef = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => {
      if (dialog?.open) dialog.close();
    };
  }, []);

  return (
    <dialog
      className="confirm-dialog"
      ref={dialogRef}
      aria-labelledby="confirm-title"
      aria-describedby="confirm-copy"
      onCancel={(event) => {
        event.preventDefault();
        onCancel();
      }}
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          onConfirm(locations);
        }}
      >
        <p className="eyebrow">ПІДТВЕРДЖЕННЯ</p>
        <h2 id="confirm-title">Запустити сценарій?</h2>
        <p id="confirm-copy" className="dialog-copy">
          Команда «{preset.name}» буде надіслана до вибраних корпусів:
        </p>
        <ul className="dialog-locations">
          {locations.map((location) => (
            <li key={location.id}>{location.name}</li>
          ))}
        </ul>
        <p className="dialog-note">
          Після підтвердження сервер збереже команду. Відтворення звуку потребує окремого
          підтвердження від вузла.
        </p>
        {error && (
          <p className="alert alert-error dialog-error" role="alert">
            {error}
          </p>
        )}
        <div className="dialog-actions">
          <button
            className="button button-secondary"
            type="button"
            onClick={onCancel}
            disabled={pending}
          >
            Скасувати
          </button>
          <button
            className="button button-primary"
            type="submit"
            disabled={pending || locations.length === 0}
          >
            {pending ? 'Надсилання…' : 'Підтвердити запуск'}
          </button>
        </div>
      </form>
    </dialog>
  );
}
