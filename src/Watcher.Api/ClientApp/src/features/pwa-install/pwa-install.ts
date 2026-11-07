export type InstallPlatform = 'ios' | 'android' | 'desktop';

export type InstallBrowser =
  'safari' | 'chrome' | 'samsung-internet' | 'edge' | 'firefox' | 'other';

export interface InstallEnvironment {
  platform: InstallPlatform;
  browser: InstallBrowser;
}

export interface InstallInstructions {
  title: string;
  steps: string[];
  note: string;
}

export function detectInstallEnvironment(
  userAgent: string,
  platform: string,
  maxTouchPoints: number,
): InstallEnvironment {
  const isIos =
    /iPhone|iPad|iPod/i.test(userAgent) || (platform === 'MacIntel' && maxTouchPoints > 1);
  const installPlatform: InstallPlatform = isIos
    ? 'ios'
    : /Android/i.test(userAgent)
      ? 'android'
      : 'desktop';

  let browser: InstallBrowser = 'other';
  if (/SamsungBrowser/i.test(userAgent)) browser = 'samsung-internet';
  else if (/Edg|EdgiOS/i.test(userAgent)) browser = 'edge';
  else if (/FxiOS|Firefox/i.test(userAgent)) browser = 'firefox';
  else if (/CriOS|Chrome/i.test(userAgent)) browser = 'chrome';
  else if (/Safari/i.test(userAgent)) browser = 'safari';

  return { platform: installPlatform, browser };
}

export function getInstallInstructions({
  platform,
  browser,
}: InstallEnvironment): InstallInstructions {
  if (platform === 'ios') {
    return {
      title: 'Додайте на початковий екран',
      steps: [
        'Натисніть «Поділитися» у меню браузера.',
        'Виберіть «На початковий екран».',
        'Перевірте назву та натисніть «Додати».',
      ],
      note: 'У Safari можна ввімкнути «Відкривати як вебзастосунок», щоб система відкривалася окремим вікном.',
    };
  }

  if (platform === 'android' && browser === 'chrome') {
    return {
      title: 'Встановіть застосунок',
      steps: [
        'Відкрийте меню Chrome — кнопка ⋮ у правому верхньому куті.',
        'Натисніть «Установити застосунок» або «Додати на головний екран».',
        'Підтвердьте встановлення або створення ярлика.',
      ],
      note: 'Назва пункту залежить від версії Chrome.',
    };
  }

  if (platform === 'android' && browser === 'samsung-internet') {
    return {
      title: 'Додайте на головний екран',
      steps: [
        'Відкрийте меню Samsung Internet.',
        'Виберіть «Додати сторінку до» → «Головний екран».',
        'Підтвердьте додавання ярлика.',
      ],
      note: 'Ярлик відкриватиме систему у вибраному браузері.',
    };
  }

  if (platform === 'android') {
    return {
      title: 'Додайте систему на головний екран',
      steps: [
        'Відкрийте меню браузера.',
        'Знайдіть «Установити застосунок», «Додати на головний екран» або схожу команду.',
        'Підтвердьте встановлення чи створення ярлика.',
      ],
      note: 'Назви команд відрізняються між браузерами. Якщо їх немає, відкрийте сайт у Chrome або Samsung Internet.',
    };
  }

  if (browser === 'safari') {
    return {
      title: 'Додайте систему в Dock',
      steps: [
        'У Safari відкрийте меню «Файл».',
        'Виберіть «Додати в Dock».',
        'Перевірте назву та натисніть «Додати».',
      ],
      note: 'Ця команда доступна в Safari на macOS Sonoma або новішій версії.',
    };
  }

  if (browser === 'chrome' || browser === 'edge') {
    return {
      title: 'Встановіть застосунок',
      steps: [
        'Натисніть значок встановлення біля адреси сайту або відкрийте меню браузера.',
        'Виберіть «Установити застосунок» або «Встановити цю сторінку як застосунок».',
        'Підтвердьте встановлення.',
      ],
      note: 'Після встановлення система відкриватиметься в окремому вікні.',
    };
  }

  return {
    title: 'Створіть ярлик системи',
    steps: [
      'Відкрийте меню браузера.',
      'Знайдіть команду «Установити застосунок», «Додати в Dock» або «Створити ярлик».',
      'Підтвердьте дію.',
    ],
    note: 'Якщо браузер не підтримує встановлення, відкрийте сайт у Chrome або Edge.',
  };
}

export function isStandaloneMode(displayModeStandalone: boolean, iosStandalone: boolean): boolean {
  return displayModeStandalone || iosStandalone;
}
