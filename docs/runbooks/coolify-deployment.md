# Розгортання в Coolify

Інструкція описує підготовку production-ресурсу `notify.kgta.edu.ua`. Вона не створює DNS-записів і не містить робочих секретів. До підтвердження DNS, адреси довіреного проксі, сертифікатів і процедури реєстрації пристроїв production-запуск не виконувати.

## Файли розгортання

У Coolify використовуйте `compose.coolify.yaml` як єдине джерело Compose-конфігурації. Базовий `compose.yaml` призначений для локальної розробки, а `compose.mtls.yaml` — для локального накладання mTLS. Coolify для Git-based Compose ресурсу завантажує один файл; окрема production-конфігурація потрібна, щоб зібрати API, приватну БД, зовнішній mTLS-порт і production-параметри в одному керованому стеку. Не вмикайте Raw deployment: стандартний режим Coolify додає мережу та proxy labels для домену.

У Coolify створіть Project та Environment, потім ресурс **Public Repository** (або Git App/Deploy Key), Build strategy **Compose**. Вкажіть:

| Поле Coolify            | Значення                                                                   |
| ----------------------- | -------------------------------------------------------------------------- |
| Repository              | `https://github.com/kgta-systems/kgta-alert`                               |
| Branch                  | `main` для production; окремий ресурс зі staging-секретами — для `develop` |
| Base Directory          | `/`                                                                        |
| Docker Compose Location | `compose.coolify.yaml`                                                     |
| Compose deployment      | стандартний керований режим, не Raw                                        |

На вкладці **Domains** додайте домен до сервісу `watcher`: `https://notify.kgta.edu.ua:8080`. `8080` — внутрішній порт контейнера; браузери звертаються до звичайного HTTPS `https://notify.kgta.edu.ua`. Після цього Coolify має додати маршрут до свого proxy та випустити TLS-сертифікат. Не додавайте домен до `postgres` чи сервісу `migrate`.

## DNS та мережа

Зміни DNS виконує адміністратор домену окремо після погодження адреси сервера Coolify:

| Запис  | Ім’я     | Значення                                                                   |
| ------ | -------- | -------------------------------------------------------------------------- |
| `A`    | `notify` | публічна IPv4-адреса сервера, на якому працює ресурс                       |
| `AAAA` | `notify` | публічна IPv6-адреса цього сервера, лише якщо IPv6-маршрут повністю працює |

Публічний firewall має пропускати TCP `80` і `443` до proxy Coolify. Порт `80` потрібен для HTTP-01/перенаправлення, `443` — для HTTPS. Якщо є і `A`, і `AAAA`, обидва маршрути повинні вести до того самого ресурсу. Перевірте обидва результати `dig +short notify.kgta.edu.ua A` та `dig +short notify.kgta.edu.ua AAAA` до запиту сертифіката. Не створюйте `AAAA`, якщо сервер або firewall не приймає IPv6.

### Довірена адреса HTTPS-проксі

Застосунок у production довіряє лише одному IP з `ReverseProxy:KnownProxies` і лише одному proxy-hop. У Compose це обов’язкова змінна `REVERSE_PROXY_IP`; CIDR і довільні мережі не підтримуються. Не задавайте `0.0.0.0/0`, адресу всього Docker bridge чи іншу широку підмережу. Якщо довірена адреса не відповідає TCP peer, forwarded-заголовки не приймаються; схема HTTPS, OAuth callback та URL webhook можуть визначатися неправильно.

Перед production:

1. Підніміть окремий staging-ресурс із тією самою схемою мережі й тестовими обліковими даними.
2. На Coolify-сервері знайдіть контейнер proxy та мережу, спільну з `watcher`: `docker inspect` покаже адреси контейнера в `NetworkSettings.Networks`.
3. Підтвердіть фактичний IP джерела з’єднання proxy до `watcher` захопленням тестового HTTPS-запиту на інтерфейсі Docker bridge (`tcpdump`) або журналом тимчасового діагностичного середовища. Адреса proxy-контейнера і фактичний peer можуть відрізнятися через мережеву конфігурацію, тому не копіюйте IP навмання.
4. Закріпіть адресу proxy на рівні підтримуваної конфігурації мережі Coolify. Після оновлення/перезапуску proxy повторно перевірте фактичний peer. Якщо Coolify не гарантує стабільну адресу, використайте окремий reverse proxy з фіксованою внутрішньою IP-адресою або відкладіть production; не розширюйте allowlist.
5. Встановіть перевірену адресу як `REVERSE_PROXY_IP` і перевірте в застосунку, що `X-Forwarded-Proto: https` приймається саме від цього proxy.

Довірений proxy повинен видаляти вхідні клієнтські `X-Forwarded-*` заголовки й записувати власні. Зовнішній HTTPS-порт — `443`. Порт `8443` для mTLS пристроїв не є web-доменом і не має проходити через HTTP proxy.

## Змінні та секрети

Додайте їх у **Environment Variables** ресурсу Coolify. Позначайте секретними поля з паролями й токенами. Не вставляйте значення у Git, Build arguments, домен, URL, скриншоти чи журнали. Файлові секрети мають бути попередньо створені на сервері та доступні контейнерному UID `1654` лише для читання; Compose очікує абсолютні шляхи до них.

| Змінна                                    | Призначення                                                                                      |
| ----------------------------------------- | ------------------------------------------------------------------------------------------------ |
| `POSTGRES_DB`                             | Назва production-бази; наприклад, `kgta_alert`                                                   |
| `POSTGRES_USER`                           | Окремий користувач застосунку, не суперкористувач `postgres`                                     |
| `POSTGRES_PASSWORD`                       | Випадковий пароль; задайте 64 hex-символи, щоб уникнути неоднозначного розбору connection string |
| `DEVICE_MTLS_BIND_ADDRESS`                | Приватна адреса хоста, на якій пристрої досягають порту `8443`; не використовуйте `0.0.0.0`      |
| `REVERSE_PROXY_IP`                        | Перевірена IP-адреса джерела з’єднання Coolify proxy, див. попередній розділ                     |
| `DATAPROTECTION_CERTIFICATE_FILE`         | Абсолютний шлях до PFX із приватним ключем для шифрування Data Protection keys                   |
| `DATAPROTECTION_CERTIFICATE_PASSWORD`     | Пароль PFX Data Protection                                                                       |
| `DEVICE_SERVER_CERTIFICATE_FILE`          | Абсолютний шлях до серверного mTLS PFX із приватним ключем та правильним SAN                     |
| `DEVICE_SERVER_CERTIFICATE_PASSWORD_FILE` | Абсолютний шлях до файла пароля серверного PFX                                                   |
| `DEVICE_CLIENT_CA_CERTIFICATE_FILE`       | Абсолютний шлях до кореневого CA, який видає клієнтські сертифікати пристроїв                    |
| `UKRAINEALARM_TOKEN`                      | Обов’язковий API-токен UkraineAlarm для callback та polling                                      |
| `ALERTS_IN_UA_TOKEN`                      | Обов’язковий токен alerts.in.ua для резервного запиту джерела                                    |
| `GOOGLE_AUTH_ENABLED`                     | `true` лише після налаштування OAuth; інакше `false`                                             |
| `GOOGLE_AUTH_CLIENT_ID`                   | OAuth Client ID, коли Google login увімкнений                                                    |
| `GOOGLE_AUTH_CLIENT_SECRET`               | OAuth Client Secret, коли Google login увімкнений                                                |

Для Google OAuth зареєструйте callback URI `https://notify.kgta.edu.ua/signin-google` у Google Cloud Console. Для UkraineAlarm callback має бути `https://notify.kgta.edu.ua/api/v1/alert-sources/ukrainealarm/webhook`; реєстрацію callback у провайдера виконайте окремою погодженою операцією. `UkraineAlarm__PublicBaseUrl` вже задається Compose як `https://notify.kgta.edu.ua`.

### Джерела тривог

Production Compose вмикає `AlertSources__Neptun__Realtime__Enabled=true`: Watcher відкриває один WebSocket до NEPTUN і використовує REST для початкового знімка та відновлення з’єднання. Це вихідний WebSocket-потік, не HTTP webhook. Не масштабуйте сервіс `watcher` на кілька реплік: локальні кеші та з’єднання джерел не мають спільної координації. UkraineAlarm webhook потрібно зареєструвати через адміністративний endpoint після перевірки домену; callback лише запускає позачерговий авторизований запит до API. Alerts.in.ua у цій схемі опитується через REST.

Coolify трактує `${NAME:?}` як обов’язкову змінну і блокує розгортання без неї. Файлові mount-и не копіюють сертифікат у образ: шлях має існувати на сервері, а UID `1654` повинен мати читання. Перевірте це до першого запуску.

## Порти, база та постійні дані

| Сервіс                | Внутрішній порт | Доступ                                                                            |
| --------------------- | --------------- | --------------------------------------------------------------------------------- |
| `watcher` HTTP        | `8080`          | лише з Coolify proxy через домен                                                  |
| `watcher` device mTLS | `8443`          | прямий TCP до приватної адреси хоста; firewall лише для затверджених адрес вузлів |
| `postgres`            | `5432`          | тільки внутрішня Compose-мережа; `ports:` не налаштовано                          |

`DEVICE_MTLS_BIND_ADDRESS` має бути адресою мережевого інтерфейсу, досяжною вузлам, а не адресою web proxy. Firewall дозволяє TCP `8443` тільки від погоджених вузлів; не додавайте mTLS listener як Coolify Domain і не проксіюйте його через `notify.kgta.edu.ua`. Перевірте фактичну публікацію порту після deploy командою `docker port` та зовнішньою перевіркою з дозволеного вузла.

`postgres-data` зберігає базу між перевипуском контейнера; `data-protection-keys` зберігає ключі cookie. Видалення volume не є rollback. Обмежте доступ до серверного сховища й налаштуйте окрему зашифровану резервну копію PostgreSQL (щоденний `pg_dump` плюс погоджена частота для RPO). Копія має зберігатися поза цим сервером.

Перед оновленням створіть й перевірте відновлення dump у тимчасову окрему базу. Не вважайте snapshot Docker volume достатнім для узгодженого backup. Не видаляйте `postgres-data` або `data-protection-keys` під час штатного redeploy.

## Міграції та порядок deploy

Міграції не запускаються автоматично при старті `watcher`. Для контрольованого запуску в Compose є вимкнений за замовчуванням сервіс `migrate` (profile `migration`) на тому самому commit. Він використовує .NET SDK та `dotnet-ef` `10.0.4`, має доступ до приватної БД у внутрішній мережі й завершується після `database update`.

Реліз виконувати в такому порядку:

1. Перевірити staging, конфігурацію секретів, сертифікати та адресу proxy. Переконатися, що для релізу є перевірений шлях повернення попереднього image.
2. Зупинити або перевести систему в погоджений режим обслуговування, якщо зміна схеми не є сумісною з попереднім кодом.
3. Зробити резервну копію PostgreSQL та перевірити, що файл не порожній і доступний поза сервером.
4. Зробити резервну копію named volume `audio-assets` тим самим backup runner-ом або окремим перевіреним snapshot. Відновлюйте asset volume й PostgreSQL як одну пару: в БД зберігаються `assetId` та hash файлів.
5. У SSH-сесії Coolify-сервера знайти робочу директорію та env-файл саме цього ресурсу; не виводити їхній вміст у термінал чи журнал. Визначити ім’я поточного Compose project за label `com.docker.compose.project` запущеного контейнера `watcher`. Запустити `docker compose -p <ім’я-проєкту> --env-file <env-файл-ресурсу> --profile migration -f <шлях-до-джерела>/compose.coolify.yaml run --build --rm migrate`. Параметр `--build` потрібен, бо migration profile не запускається під час звичайного deploy. Сервіс сам виконує `database update` для `Watcher.Infrastructure` з `Watcher.Api` як startup project. Перед запуском перевірити `docker compose ... config --quiet` із тими самими параметрами. Не запускайте команду з локального комп’ютера або без `-p`: вона має використати поточний stack project та його приватну мережу, а не створити новий стек із порожньою БД.
6. Переконатися за кодом завершення та журналом, що міграція пройшла; перевірити таблицю `__EFMigrationsHistory`.
7. Розгорнути commit у Coolify. Дочекатися healthy `watcher` і `postgres`; відкрити `https://notify.kgta.edu.ua`, перевірити `GET /api/v1/health/ready`, вхід, callback provider та критичний сценарій тестової команди.
8. Перевірити, що PostgreSQL і порт `8443` не доступні з публічної мережі, а доступ вузлів проходить лише за mTLS.

Не запускайте міграцію, якщо не підтверджено, що команда підключена до production Compose network і тієї самої БД. Якщо розташування активного source/env-файла або stack project не можна однозначно визначити за даними ресурсу та Docker labels, зупиніться й узгодьте release runner, підключений до приватної мережі. Не вгадуйте шлях, не запускайте команду без потрібного `.env` і не відкривайте PostgreSQL в інтернет для зручності.

Нові міграції мають бути backward-compatible: спершу додавання nullable/нових структур, потім код, що їх використовує; видалення/перейменування — лише окремим наступним релізом після перевірки. Не допускайте одночасної роботи старого image з несумісною схемою.

## Health checks

Compose визначає окремі перевірки:

- `postgres`: `pg_isready` з інтервалом 5 секунд, до 10 невдалих спроб;
- `watcher`: `GET http://127.0.0.1:8080/api/v1/health/ready`, раз на 30 секунд, timeout 5 секунд, 3 спроби після стартового вікна 45 секунд.

Endpoint готовності відповідає `200` лише коли PostgreSQL доступна. Health check працює всередині контейнера через `curl`, встановлений у runtime-образі. Він не перевіряє доступність зовнішніх provider API, Google OAuth, DNS або вузлів — ці компоненти перевіряйте окремо після deploy.

## Runtime-журнали контейнерів

Для `watcher`, `postgres` і одноразового сервісу `migrate` Compose використовує Docker `local` logging driver з ротацією: кожен файл обмежений 10 MiB, зберігається не більше п’яти файлів на контейнер. Це обмежує дисковий простір технічних журналів приблизно 50 MiB на контейнер. Не вимикайте ротацію для економії часу; перед зміною лімітів оцініть кількість одночасних інцидентів і потребу в локальній діагностиці.

Ротація стосується лише stdout/stderr контейнера. Вона не видаляє записи `audit_events`, команди, доставки чи квитанції з PostgreSQL і не є політикою зберігання аудиту. Якщо потрібен довший строк runtime-журналів, використовуйте погоджене зовнішнє сховище з контролем доступу та окремою політикою retention; не збільшуйте безмежно Docker logs на сервері.

## Відкат

1. Якщо контейнер не став healthy, перевірте Runtime/Deployment logs, причину health check і доступність БД. Не вимикайте health check для обходу проблеми.
2. Coolify rollback повертає попередній image, але не скасовує міграцію, не відновлює PostgreSQL і не змінює файли сертифікатів. Старий image допускається лише за backward-compatible схемою.
3. Якщо потрібен відкат схеми, зупиніть записи, оцініть наслідки, відновіть перевірений dump в окрему БД і переключайтеся на неї лише після валідації. Не виконуйте руйнівний `database update <older-migration>` на робочих даних без окремого плану.
4. Перевірте `https://notify.kgta.edu.ua`, endpoint готовності, авторизацію та журнал аудиту. Зафіксуйте commit/image, причину та результат.

Після rollback зберігайте всі volume (`postgres-data`, `data-protection-keys`, `audio-assets`). Coolify image rollback не повертає зміни в persistent storage чи БД; це потрібно планувати окремо.

## Перевірки до production

- DNS A/AAAA і firewall підтверджені; TCP 80/443 доступні ззовні.
- Coolify має домен `https://notify.kgta.edu.ua:8080` і валідний TLS.
- `REVERSE_PROXY_IP` — фактичний, стабільний, точковий IP; тест довіри до forwarded headers пройшов.
- Усі bind-mounted файли існують, мають правильний формат і читаються UID контейнера; приватні ключі недоступні іншим користувачам.
- `postgres` не має host ports; зовнішня перевірка 5432 не проходить.
- `8443` доступний лише із затвердженої мережі вузлів і не проходить через HTTP proxy.
- Резервний dump відновлено у тимчасове середовище; збережено процедуру повернення.
- **Production onboarding пристроїв поки недоступний**: до затвердження окремого процесу випуску та реєстрації сертифікатів використовуйте лише тестові вузли й тестові сертифікати.

## Офіційна документація Coolify

- [Docker Compose applications](https://coolify.io/docs/applications/builds/docker-compose) — один шлях до Compose-файлу, service domains, managed proxy labels, network і перевірки стану.
- [Domains](https://coolify.io/docs/core/networking/domains) та [DNS](https://coolify.io/docs/core/networking/dns) — домен, внутрішній порт, записи A/AAAA і вхідний TCP 80/443.
- [Environment variables](https://coolify.io/docs/services/configuration/environment-variables) — `${NAME:?}` для обов’язкових значень та зберігання секретів.
- [Persistent storage](https://coolify.io/docs/services/configuration/persistent-storage) — Compose mounts є джерелом істини; named volume зберігає дані між контейнерами, але не замінює backup.
- [Health checks](https://coolify.io/docs/applications/configuration/health-checks) — Compose-застосунок задає перевірки в Compose або Dockerfile; HTTP check потребує `curl`/`wget` у контейнері.
- [Rollbacks](https://coolify.io/docs/applications/deployments/rollbacks) — image rollback не скасовує міграції, persistent data або зовнішні зміни.

Актуальність версії образу PostgreSQL звіряйте з [політикою версій PostgreSQL](https://www.postgresql.org/support/versioning/) та [офіційними Docker Hub tags](https://hub.docker.com/_/postgres/tags?name=16-alpine). В Compose зафіксовано `postgres:16.15-alpine`, останній доступний patch для підтримуваної major-версії 16 на дату підготовки цього документа.
