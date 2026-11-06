# Автоматизовані перевірки

## Рівні тестування

- `Watcher.UnitTests` перевіряє валідацію запитів, права доступу, ідемпотентність, доступ до опублікованих сценаріїв, аудит команд і модель EF Core.
- `Watcher.Api.IntegrationTests` запускає API через `WebApplicationFactory` і перевіряє HTTP-відповіді, cookie, anti-forgery та аудит входу й виходу.
- `PostgresAlertCommandApiTests` використовує справжній PostgreSQL: міграції, запити EF Core, JSONB, індекси й зовнішні ключі перевіряються провайдером, який використовує застосунок.
- `tests/ui` перевіряє повторне використання ключа ідемпотентності командами пульта за допомогою вбудованого тестового засобу Node.js.
- `src/Watcher.Api/ClientApp/src` містить Vitest-тести API-клієнта, ролей, станів інтерфейсу, команд, відновлення команди з `sessionStorage`, журналу аудиту та політики Service Worker у jsdom.
- `CommandDeliveriesEndpointTests` перевіряє агреговані стани доставок за всіма сторінками; `CommandDeliveriesPanel.test.tsx` перевіряє доступність STOP і підсумок підтверджень вузлів. `DeviceCommandProcessorTests` перевіряє, що ранній STOP фіксує sequence й відхиляє запізнілий START.
- `MinuteOfSilenceScheduleTests` перевіряє час 09:00 у `Europe/Kyiv`, переходи літнього часу й відмову від запуску після пропущеного вікна. PostgreSQL-тести перевіряють строк дії команди, коректність manifest hash, запис пропущеної дати, причини пропуску та конкуренцію кількох обробників за одну дату.
- `CommandDeliveryMaterializerTests` використовує PostgreSQL для перевірки fan-out за активними корпусами й незаблокованими пристроями, обробки строку дії та конкуренції за один outbox запис.

## Запуск

Швидкий набір не потребує Docker:

```bash
dotnet test KgtaAlert.slnx --configuration Release
node --test tests/ui/*.test.js
npm ci
npm run lint
npm run format:check
npm run typecheck
npm test
npm run build
```

`npm run build` генерує React bundle в `src/Watcher.Api/wwwroot/dist` і Service Worker у `wwwroot`. Це результати збірки, які не зберігаються в Git; Dockerfile збирає їх перед `dotnet publish`.

PostgreSQL-набір запускається, коли задано `ConnectionStrings__Watcher`. Він створює окрему схему з випадковою назвою, застосовує всі міграції, а після завершення видаляє схему. Для локального запуску спершу підніміть БД через Compose й передайте рядок підключення:

```bash
docker compose up -d postgres
ConnectionStrings__Watcher='Host=localhost;Port=5432;Database=kgta_alert;Username=kgta_alert;Password=change-me-for-local-development' \
  dotnet test tests/Watcher.Api.IntegrationTests/Watcher.Api.IntegrationTests.csproj --configuration Release
```

Не використовуйте для інтеграційних тестів робочу базу: набір створює й видаляє окрему схему.

## CI

Перед .NET-перевірками CI запускає `npm ci`, lint, перевірку форматування й типів, Vitest та production-збірку фронтенду.

Звичайна збірка запускає unit-тести, HTTP-тести без бази, форматування й перевірку залежностей. Окреме завдання піднімає PostgreSQL 16, застосовує міграції й запускає інтеграційний набір. Після успішних unit та PostgreSQL jobs CI об'єднує Cobertura-звіти за файлом і номером рядка та вимагає щонайменше 70% покриття рядків у кожному модулі зі списку в workflow. Це дає змогу врахувати різні тести одного коду без подвійного рахунку. До обов'язкового набору входять `Watcher.Api`, `Watcher.Contracts`, `Watcher.Domain`, `Watcher.Infrastructure`, `Watcher.EdgeAgent.Contracts`, `Watcher.EdgeAgent.Core` та `Watcher.EdgeAgent.Infrastructure`. Збірка-служба `Watcher.EdgeAgent.Service` і WPF-панель `Watcher.EdgeAgent.Desktop` не входять до coverage gate: їхні сценарії перевіряються окремими Windows integration tests та перевіркою збірки відповідно. Edge Agent також має окрему перевірку не менше 70% сумарного покриття `Watcher.EdgeAgent.Core` та `Watcher.EdgeAgent.Infrastructure`. Пороги перевіряються CI-скриптами, а не параметрами VSTest collector. Звіти виключають згенеровані файли міграцій; їхня працездатність окремо перевіряється застосуванням усіх міграцій до чистого PostgreSQL. У зведенні GitHub Actions показуються відсотки покриття рядків і гілок за production-модулями. Завдання PostgreSQL також виводить кількість пройдених, пропущених і невдалих тестів та завершується помилкою, якщо набір не підтвердив реальне виконання без пропусків.

Покриття слугує індикатором. Нові тести мають перевіряти зовнішню поведінку, межі доступу, помилки й конкурентні умови, а не збільшувати відсоток без перевірки ризиків системи.
