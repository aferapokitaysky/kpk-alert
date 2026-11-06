# Розгортання Windows-вузла

Служба й панель публікуються окремо. Перед встановленням підготуйте чинний клієнтський сертифікат у `LocalMachine\My`, його SHA-1 thumbprint і `edge-agent.json` на основі `edge-agent.example.json`. Приватний ключ не експортуйте й не додавайте до репозиторію.

```powershell
dotnet publish src/Watcher.EdgeAgent.Service/Watcher.EdgeAgent.Service.csproj -c Release -r win-x64 --self-contained true -o publish/edge-agent
dotnet publish src/Watcher.EdgeAgent.Desktop/Watcher.EdgeAgent.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/edge-agent-desktop
```

Перевірки Named Pipe та сценаріїв запуску/відновлення виконуються окремим Windows-набором:

```powershell
dotnet test tests/Watcher.EdgeAgent.Service.IntegrationTests/Watcher.EdgeAgent.Service.IntegrationTests.csproj -c Release -p:RunWindowsIntegrationTests=true
```

Інсталятор перевіряє чинний сертифікат у `LocalMachine\My`, створює локальну групу `KGTA Alert Operators` і встановлює службу під віртуальним обліковим записом `NT SERVICE\KGTA.Alert.EdgeAgent`. До Named Pipe мають доступ ця служба, адміністратори та лише учасники групи операторів. Додайте потрібних користувачів до групи; вона зберігається після видалення програми:

```powershell
Add-LocalGroupMember -Group 'KGTA Alert Operators' -Member 'DOMAIN\operator'
```

Перед першим запуском відкрийте `certlm.msc`, знайдіть сертифікат пристрою в «Особисте → Сертифікати», виберіть «Керування приватними ключами» й надайте `NT SERVICE\KGTA.Alert.EdgeAgent` право читання. Інсталятор не експортує ключ і не змінює його ACL. Для цього використайте `-SkipStart`, надайте право, а потім запустіть службу.

Запустіть PowerShell від адміністратора з каталогу репозиторію:

```powershell
./src/Watcher.EdgeAgent.Service/install-service.ps1 `
  -PublishDirectory ./publish/edge-agent `
  -DesktopPublishDirectory ./publish/edge-agent-desktop `
  -ConfigurationFile ./edge-agent.json `
  -OperatorMembers 'DOMAIN\operator' `
  -InteractiveUser 'DOMAIN\operator' `
  -SkipStart
```

`-OperatorMembers` додає задані облікові записи до локальної групи під час встановлення; повторний запуск безпечно пропускає вже доданих учасників. `-InteractiveUser` визначає єдиний обліковий запис, для якого панель запускається після входу у Windows. За замовчуванням використовується обліковий запис поточного токена PowerShell; якщо інсталятор запускає інший адміністратор або `SYSTEM`, передайте обліковий запис інтерактивного оператора явно. Його профіль має бути завантажений. Інсталятор додає цього користувача до `KGTA Alert Operators`, щоб панель могла читати стан служби.

Служба налаштовується через Windows Service Control Manager на автоматичний запуск після завантаження системи. WPF-панель реєструється окремо тільки для вибраного користувача в `HKEY_USERS\<SID>\Software\Microsoft\Windows\CurrentVersion\Run` зі шляхом до exe в лапках. Запис `HKLM\...\Run` не створюється, тому панель не стартує у всіх користувацьких сеансах. Конфігурація залишається у `%ProgramData%\KGTA\Alert\edge-agent.json`, приватний ключ — у Windows Certificate Store; секрети до Run-ключа не записуються.

З параметром `-SkipStart` служба лишається зупиненою до ручного запуску або перезавантаження, але її тип запуску вже `Automatic`; health check до ручного запуску не виконується. Після налаштування доступу до приватного ключа запустіть службу:

```powershell
Start-Service -Name 'KGTA.Alert.EdgeAgent'
```

Інсталятор копіює конфігурацію в `%ProgramData%\KGTA\Alert\edge-agent.json` лише під час першої інсталяції. При оновленні він зберігає наявну конфігурацію, базу, аудіофайли й журнали. Панель доступна в меню «Пуск» → «KGTA Alert»; її закриття не зупиняє службу.

Підготовка аудіопакетів і налаштування `AudioEndpointId` мають відповідати окремій специфікації вузла. Перевірте відтворення через фізичний вихід на цільовому обладнанні: WASAPI в Windows Session 0 потребує апаратної перевірки. Поточний серверний API також має окремі блокери для контракту маніфесту та команди зупинки; успішна інсталяція не означає готовність до бойового розгортання.

Щоб видалити службу й панель, не видаляючи локальні дані:

```powershell
./src/Watcher.EdgeAgent.Service/uninstall-service.ps1 -InteractiveUser 'DOMAIN\operator'
```

Перед видаленням інсталятор прибирає лише власне значення панелі з Run-розділу цього користувача. Виконуйте видалення з тим самим `-InteractiveUser`, що й установлення, та за завантаженого профілю. Локальні дані видаляються лише за окремим явним параметром `-RemoveLocalData`. Група операторів і призначені їй користувачі залишаються.
