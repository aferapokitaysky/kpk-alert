import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PwaInstallGuidance } from './PwaInstallGuidance';

function makeInstallPrompt(outcome: 'accepted' | 'dismissed' = 'accepted') {
  const event = new Event('beforeinstallprompt', { cancelable: true });
  const prompt = vi.fn().mockResolvedValue(undefined);
  Object.assign(event, {
    prompt,
    userChoice: Promise.resolve({ outcome, platform: 'webapp' }),
  });
  return { event, prompt };
}

function setDisplayMode(isStandalone: boolean) {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: isStandalone && query === '(display-mode: standalone)',
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

describe('PwaInstallGuidance', () => {
  beforeEach(() => {
    setDisplayMode(false);
    Object.defineProperty(navigator, 'userAgent', {
      configurable: true,
      value: 'Mozilla/5.0 (Linux; Android 15) Chrome/135.0',
    });
    Object.defineProperty(navigator, 'platform', { configurable: true, value: 'Linux' });
    Object.defineProperty(navigator, 'maxTouchPoints', { configurable: true, value: 5 });
    Object.defineProperty(navigator, 'standalone', { configurable: true, value: false });
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('shows the college logo and opens Android Chrome instructions after a click', async () => {
    const user = userEvent.setup();
    render(<PwaInstallGuidance />);

    expect(document.querySelector('.pwa-install__logo')).toHaveAttribute(
      'src',
      '/images/kpk-logo.png',
    );
    await user.click(screen.getByRole('button', { name: 'Як додати' }));

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Встановіть застосунок' })).toBeInTheDocument();
    expect(screen.getByText(/меню Chrome/)).toBeInTheDocument();
  });

  it('does not open the native install prompt until the user clicks', async () => {
    const user = userEvent.setup();
    const { event, prompt } = makeInstallPrompt();
    render(<PwaInstallGuidance />);
    fireEvent(window, event);

    expect(event.defaultPrevented).toBe(true);
    expect(prompt).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Встановити' }));

    expect(prompt).toHaveBeenCalledOnce();
    await waitFor(() => expect(screen.queryByRole('region')).not.toBeInTheDocument());
  });

  it('falls back to instructions when the user dismisses the native prompt', async () => {
    const user = userEvent.setup();
    const { event, prompt } = makeInstallPrompt('dismissed');
    render(<PwaInstallGuidance />);
    fireEvent(window, event);
    await user.click(screen.getByRole('button', { name: 'Встановити' }));

    expect(prompt).toHaveBeenCalledOnce();
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
  });

  it('hides the guidance in an installed standalone app', () => {
    setDisplayMode(true);

    render(<PwaInstallGuidance />);

    expect(screen.queryByRole('region')).not.toBeInTheDocument();
  });

  it('hides the guidance after appinstalled', async () => {
    render(<PwaInstallGuidance />);
    fireEvent(window, new Event('appinstalled'));

    await waitFor(() => expect(screen.queryByRole('region')).not.toBeInTheDocument());
  });

  it('closes the instructions through the labelled close button and escape key', async () => {
    const user = userEvent.setup();
    render(<PwaInstallGuidance />);
    await user.click(screen.getByRole('button', { name: 'Як додати' }));
    const dialog = screen.getByRole('dialog');

    await user.click(screen.getByRole('button', { name: 'Закрити інструкцію' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Як додати' }));
    fireEvent(dialog, new Event('cancel', { bubbles: false, cancelable: true }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });
});
