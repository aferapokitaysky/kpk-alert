# Структура бази даних

## Загальні правила

- PostgreSQL є авторитетним сховищем Watcher. SQLite вузла містить лише локальні дані, необхідні для роботи під час втрати зв’язку.
- Дати й час зберігаються як `timestamptz` у UTC.
- Ідентифікатори сутностей — UUID. Публічний код локації та код сценарію мають окремі унікальні обмеження.
- Секрети не зберігаються відкритим текстом. Аудитові записи не змінюються звичайними операціями застосунку.
- Зміни схеми виконуються міграціями EF Core. Ручні зміни production-бази не допускаються.

## Сутності та зв’язки

```mermaid
erDiagram
  users ||--o{ user_roles : має
  roles ||--o{ user_roles : надає
  locations ||--o{ devices : містить
  locations ||--o{ location_presets : дозволяє
  sound_presets ||--o{ location_presets : призначений
  alert_commands ||--o{ command_targets : надсилається
  locations ||--o{ command_targets : отримує
  alert_commands ||--o{ command_receipts : має
  devices ||--o{ command_receipts : підтверджує
  users ||--o{ audit_events : виконує
```

| Таблиця | Основні поля | Призначення |
| --- | --- | --- |
| `users` | `id`, `external_subject`, `email`, `is_active` | Облікові записи з довіреного постачальника входу |
| `roles` | `id`, `code` | Ролі `admin`, `sound_engineer`, `notificator`, `auditor` |
| `user_roles` | `user_id`, `role_id`, `location_id?` | Глобальні права або права в конкретній локації |
| `locations` | `id`, `code`, `name`, `is_active` | Корпус або окрема зона трансляції |
| `devices` | `id`, `location_id`, `certificate_thumbprint`, `status` | Зареєстровані вузли та стан сертифіката |
| `sound_presets` | `id`, `code`, `priority`, `manifest_hash`, `status` | Погоджені сценарії відтворення |
| `location_presets` | `location_id`, `preset_id`, `is_enabled` | Сценарії, дозволені для локації |
| `alert_commands` | `id`, `kind`, `preset_id`, `initiated_by_user_id`, `created_at` | Створені команди та ініціатор |
| `command_targets` | `command_id`, `location_id`, `sequence`, `state` | Намір і порядковий номер команди в кожній локації |
| `location_command_sequences` | `location_id`, `next_sequence` | Транзакційний лічильник наступного номера команди для локації |
| `command_receipts` | `id`, `command_id`, `device_id`, `status`, `occurred_at` | Підтвердження отримання, запуску та завершення |
| `audit_events` | `id`, `event_type`, `actor_type`, `actor_id`, `payload`, `occurred_at` | Історія дій користувачів і системи |
| `outbox_messages` | `id`, `command_id`, `event_type`, `payload`, `created_at_utc`, `processed_at_utc`, `attempts` | Надійна черга подій для подальшої доставки |
| `device_sessions` | `id`, `device_id`, `connected_at`, `last_seen_at` | Історія технічних з’єднань |

Фізичну схему підтримують EF Core migrations. Кожне повідомлення outbox посилається на одну команду; унікальний індекс не дозволяє створити для неї дубльовану подію. Непроцесовані записи читаються за індексом стану й часу створення.

Outbox фіксує намір доставки в тій самій транзакції, що команду, цілі та аудит. API повертає `202 Accepted` після успішного commit. Фоновий worker періодично матеріалізує непроцесовані події `alert_command.created` у записи `command_deliveries`; він не надсилає команди вузлам і не змінює політику повторів. Параметри `CommandOutboxWorker:Enabled`, `PollInterval` і `BatchSize` керують роботою; допустимий інтервал — від 100 мс до 5 хвилин, пакет — від 1 до 500 подій. За замовчуванням worker увімкнений, інтервал становить 10 секунд, пакет — 50 подій.

Worker використовує нову DI scope для кожного циклу та обробляє події послідовно. Матеріалізатор блокує рядок outbox у PostgreSQL, тому кілька реплік можуть безпечно побачити ту саму подію: одна створює доставки, інші отримують `AlreadyProcessed`. Після помилки обробка поточного пакета зупиняється; подія залишається непроцесованою і буде повторно прочитана в наступному циклі. Лічильник `attempts` не змінюється. Повідомлення самого worker-а містять лише тип помилки та ID outbox-запису: вони не містять текст винятку, payload чи секретів. Події інших типів worker не вибирає.

`OutboxMessage.ProcessedAtUtc` означає лише, що записи доставок створені. Для мережевого надсилання команд вузлам потрібен окремий transport worker; до його реалізації `command_deliveries` залишаються в `Pending`.

Поточні події автентифікації — `auth.login.succeeded`, `auth.login.rejected` і `auth.logout`. Успішна дія містить ID локального користувача; відхилена — лише узагальнену причину. Записи містять спосіб входу та час UTC; email, зовнішній subject, Google-токени, IP-адреси й cookie до payload не додаються.

## Обмеження та індекси

- Унікальні значення: `locations.code`, `roles.code`, `sound_presets.code`, `users.external_subject`.
- Пара `(command_id, location_id)` у `command_targets` унікальна.
- Пара `(location_id, sequence)` у `command_targets` унікальна; номер завжди додатний.
- Лічильник локації змінюється атомарно в тій самій транзакції, що команда, ціль, outbox та аудит.
- Міграція послідовностей є forward-only: downgrade видалив би підтверджений порядок команд. Попередня версія застосунку може залишити схему без змін: тригер БД присвоїть номер цілям, вставленим без поля `sequence`.
- Індекс за часом у `audit_events` та `alert_commands` потрібен для перегляду історії.
- Індекс `(device_id, last_seen_at)` допомагає показувати останній зв’язок вузла.
- Відкликання доступу фіксується полем стану або `revoked_at`; історичні команди й аудит не видаляються штатним інтерфейсом.

## Локальна база вузла

| Таблиця | Вміст |
| --- | --- |
| `processed_commands` | Ідентифікатори команд і збережений результат дедуплікації |
| `outbox_events` | Непідтверджені події для надсилання після відновлення зв’язку |
| `configuration_snapshot` | Остання перевірена версія локальних налаштувань |
| `asset_manifest` | Дозволені аудіофайли та їхні SHA-256 відбитки |

Аудіофайли зберігаються окремо. Вузол перевіряє їхній відбиток перед публікацією або відтворенням.
