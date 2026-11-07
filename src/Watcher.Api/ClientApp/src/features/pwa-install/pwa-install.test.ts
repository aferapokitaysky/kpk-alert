import { describe, expect, it } from 'vitest';
import { detectInstallEnvironment, getInstallInstructions, isStandaloneMode } from './pwa-install';

describe('detectInstallEnvironment', () => {
  it('detects iPhone Chrome separately from Safari', () => {
    expect(
      detectInstallEnvironment(
        'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) CriOS/135.0 Mobile/15E148 Safari/604.1',
        'iPhone',
        5,
      ),
    ).toEqual({ platform: 'ios', browser: 'chrome' });
  });

  it('detects iPadOS devices that identify as desktop Macs', () => {
    expect(detectInstallEnvironment('Mozilla/5.0 (Macintosh) Safari/605.1', 'MacIntel', 5)).toEqual(
      { platform: 'ios', browser: 'safari' },
    );
  });

  it('detects Android Chrome and Samsung Internet', () => {
    expect(
      detectInstallEnvironment('Mozilla/5.0 (Linux; Android 15) Chrome/135.0', 'Linux', 0),
    ).toEqual({ platform: 'android', browser: 'chrome' });
    expect(
      detectInstallEnvironment(
        'Mozilla/5.0 (Linux; Android 15) SamsungBrowser/28.0 Chrome/135.0',
        'Linux',
        0,
      ),
    ).toEqual({ platform: 'android', browser: 'samsung-internet' });
  });

  it('detects desktop Edge and Safari', () => {
    expect(detectInstallEnvironment('Mozilla/5.0 Windows NT 10.0 Edg/135.0', 'Win32', 0)).toEqual({
      platform: 'desktop',
      browser: 'edge',
    });
    expect(detectInstallEnvironment('Mozilla/5.0 Macintosh Safari/605.1', 'MacIntel', 0)).toEqual({
      platform: 'desktop',
      browser: 'safari',
    });
  });
});

describe('getInstallInstructions', () => {
  it('shows the iPhone and iPad share-sheet steps', () => {
    const instructions = getInstallInstructions({ platform: 'ios', browser: 'safari' });

    expect(instructions.steps).toEqual([
      'Натисніть «Поділитися» у меню браузера.',
      'Виберіть «На початковий екран».',
      'Перевірте назву та натисніть «Додати».',
    ]);
    expect(instructions.note).toContain('вебзастосунок');
  });

  it('shows Chrome-specific Android steps', () => {
    const instructions = getInstallInstructions({ platform: 'android', browser: 'chrome' });

    expect(instructions.steps.join(' ')).toContain('Chrome');
    expect(instructions.steps.join(' ')).toContain('Додати на головний екран');
  });

  it('shows Samsung Internet-specific Android steps', () => {
    const instructions = getInstallInstructions({
      platform: 'android',
      browser: 'samsung-internet',
    });

    expect(instructions.steps.join(' ')).toContain('Samsung Internet');
    expect(instructions.steps.join(' ')).toContain('Головний екран');
  });

  it('provides a fallback for other Android browsers', () => {
    const instructions = getInstallInstructions({ platform: 'android', browser: 'firefox' });

    expect(instructions.steps.join(' ')).toContain('меню браузера');
    expect(instructions.note).toContain('Chrome');
  });

  it('provides browser-aware desktop instructions', () => {
    expect(
      getInstallInstructions({ platform: 'desktop', browser: 'safari' }).steps.join(' '),
    ).toContain('Dock');
    expect(
      getInstallInstructions({ platform: 'desktop', browser: 'chrome' }).steps.join(' '),
    ).toContain('адреси сайту');
    expect(
      getInstallInstructions({ platform: 'desktop', browser: 'other' }).steps.join(' '),
    ).toContain('«Створити ярлик»');
  });
});

describe('isStandaloneMode', () => {
  it('recognizes display-mode and the iOS standalone flag', () => {
    expect(isStandaloneMode(true, false)).toBe(true);
    expect(isStandaloneMode(false, true)).toBe(true);
    expect(isStandaloneMode(false, false)).toBe(false);
  });
});
