import { useEffect, useRef } from 'react';
import type { Location } from '../../api';

interface Props {
  commandId: string;
  locations: Location[];
  pending: boolean;
  error: string;
  onCancel: () => void;
  onConfirm: (commandId: string, locations: Location[]) => void;
}

export function StopCommandDialog({
  commandId,
  locations,
  pending,
  error,
  onCancel,
  onConfirm,
}: Props) {
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
      aria-labelledby="stop-confirm-title"
      aria-describedby="stop-confirm-copy"
      onCancel={(event) => {
        event.preventDefault();
        onCancel();
      }}
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          onConfirm(commandId, locations);
        }}
      >
        <p className="eyebrow">ПІДТВЕРДЖЕННЯ</p>
        <h2 id="stop-confirm-title">Надіслати команду «Відбій»?</h2>
        <p id="stop-confirm-copy" className="dialog-copy">
          Зупинити відтворення команди у вибраних корпусах:
        </p>
        <ul className="dialog-locations">
          {locations.map((location) => (
            <li key={location.id}>{location.name}</li>
          ))}
        </ul>
        <p className="dialog-note dialog-note-warning">
          Команда надійде всім вузлам, яким передавали запуск. Якщо відтворення вже завершилося,
          вузол безпечно підтвердить відбій без повторного запуску звуку.
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
            className="button button-danger"
            type="submit"
            disabled={pending || locations.length === 0}
          >
            {pending ? 'Надсилання…' : 'Підтвердити відбій'}
          </button>
        </div>
      </form>
    </dialog>
  );
}
