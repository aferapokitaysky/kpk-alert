import { useEffect, useRef, useState } from 'react';
import { detectInstallEnvironment, getInstallInstructions, isStandaloneMode } from './pwa-install';
import './pwa-install.css';

interface DeferredInstallPrompt extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed'; platform: string }>;
}

function readInstalledMode(): boolean {
  const displayModeStandalone =
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(display-mode: standalone)').matches;
  const iosStandalone = (navigator as Navigator & { standalone?: boolean }).standalone === true;
  return isStandaloneMode(displayModeStandalone, iosStandalone);
}

export function PwaInstallGuidance() {
  const [installed, setInstalled] = useState(readInstalledMode);
  const [installPrompt, setInstallPrompt] = useState<DeferredInstallPrompt | null>(null);
  const [instructionsOpen, setInstructionsOpen] = useState(false);
  const dialogRef = useRef<HTMLDialogElement>(null);
  const environment = detectInstallEnvironment(
    navigator.userAgent,
    navigator.platform,
    navigator.maxTouchPoints,
  );
  const instructions = getInstallInstructions(environment);

  useEffect(() => {
    const handleBeforeInstallPrompt = (event: Event) => {
      event.preventDefault();
      setInstallPrompt(event as DeferredInstallPrompt);
    };
    const handleAppInstalled = () => {
      setInstallPrompt(null);
      setInstructionsOpen(false);
      setInstalled(true);
    };

    window.addEventListener('beforeinstallprompt', handleBeforeInstallPrompt);
    window.addEventListener('appinstalled', handleAppInstalled);
    return () => {
      window.removeEventListener('beforeinstallprompt', handleBeforeInstallPrompt);
      window.removeEventListener('appinstalled', handleAppInstalled);
    };
  }, []);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog || !instructionsOpen) return;

    if (typeof dialog.showModal === 'function') dialog.showModal();
    else dialog.setAttribute('open', '');

    return () => {
      if (dialog.open && typeof dialog.close === 'function') dialog.close();
      else dialog.removeAttribute('open');
    };
  }, [instructionsOpen]);

  async function handleInstallClick() {
    if (!installPrompt) {
      setInstructionsOpen(true);
      return;
    }

    const prompt = installPrompt;
    setInstallPrompt(null);
    try {
      await prompt.prompt();
      const choice = await prompt.userChoice;
      if (choice.outcome === 'accepted') setInstalled(true);
      else setInstructionsOpen(true);
    } catch {
      setInstructionsOpen(true);
    }
  }

  if (installed) return null;

  return (
    <>
      <section className="pwa-install" aria-labelledby="pwa-install-title">
        <img className="pwa-install__logo" src="/images/kpk-logo.png" alt="" />
        <div className="pwa-install__copy">
          <h2 id="pwa-install-title">Додайте систему на головний екран</h2>
          <p>Відкривайте «Оповіщення КГТА» окремим вікном одним дотиком.</p>
        </div>
        <button className="pwa-install__action" type="button" onClick={handleInstallClick}>
          {installPrompt ? 'Встановити' : 'Як додати'}
        </button>
      </section>

      <dialog
        className="pwa-install__dialog"
        ref={dialogRef}
        aria-labelledby="pwa-install-dialog-title"
        aria-describedby="pwa-install-dialog-note"
        onCancel={(event) => {
          event.preventDefault();
          setInstructionsOpen(false);
        }}
        onClick={(event) => {
          if (event.target === event.currentTarget) setInstructionsOpen(false);
        }}
      >
        <div className="pwa-install__dialog-heading">
          <img src="/images/kpk-logo.png" alt="" />
          <div>
            <p className="pwa-install__eyebrow">ОПОВІЩЕННЯ КГТА</p>
            <h2 id="pwa-install-dialog-title">{instructions.title}</h2>
          </div>
          <button
            className="pwa-install__close"
            type="button"
            aria-label="Закрити інструкцію"
            onClick={() => setInstructionsOpen(false)}
          >
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="m6 6 12 12M18 6 6 18" />
            </svg>
          </button>
        </div>
        <ol className="pwa-install__steps">
          {instructions.steps.map((step) => (
            <li key={step}>{step}</li>
          ))}
        </ol>
        <p className="pwa-install__note" id="pwa-install-dialog-note">
          {instructions.note}
        </p>
      </dialog>
    </>
  );
}
