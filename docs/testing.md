# Автоматизовані перевірки

## Рівні тестування

- `Watcher.UnitTests` перевіряє валідацію запитів, права доступу, ідемпотентність, контракти та модель EF Core.
- `Watcher.Api.IntegrationTests` запускає API через `WebApplicationFactory` і перевіряє HTTP-відповіді.
- `PostgresAlertCommandApiTests` використовує справжній PostgreSQL: міграції, запити EF Core, JSONB, індекси й зовнішні ключі перевіряються провайдером, який використовує застосунок.

## Запуск

Швидкий набір не потребує Docker:

```bash
dotnet test KgtaAlert.slnx --configuration Release
```

PostgreSQL-набір запускається, коли задано `ConnectionStrings__Watcher`. Він створює окрему схему з випадковою назвою, застосовує всі міграції, а після завершення видаляє схему. Для локального запуску спершу підніміть БД через Compose й передайте рядок підключення:

```bash
docker compose up -d postgres
ConnectionStrings__Watcher='Host=localhost;Port=5432;Database=kgta_alert;Username=kgta_alert;Password=change-me-for-local-development' \
  dotnet test tests/Watcher.Api.IntegrationTests/Watcher.Api.IntegrationTests.csproj --configuration Release
```

Не використовуйте для інтеграційних тестів робочу базу: набір створює й видаляє окрему схему.

## CI

Звичайна збірка запускає unit-тести, HTTP-тести без бази, форматування й перевірку залежностей. CI додатково вимагає щонайменше 60% покриття рядків у кожному production-модулі під час unit-тестів. Окреме завдання піднімає PostgreSQL 16, застосовує міграції й запускає інтеграційний набір із підключенням до цієї БД. Cobertura-звіти обох наборів зберігаються як артефакти workflow. Звіти виключають згенеровані файли міграцій; їхня працездатність окремо перевіряється застосуванням усіх міграцій до чистого PostgreSQL.

Покриття слугує індикатором. Нові тести мають перевіряти зовнішню поведінку, межі доступу, помилки й конкурентні умови, а не збільшувати відсоток без перевірки ризиків системи.
