$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\EdgeAgentStartupRegistration.psm1'
$installerPath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\install-service.ps1'
Import-Module -Name $modulePath -Force

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if ($Expected -cne $Actual) {
        throw "$Message Очікувалося '$Expected', отримано '$Actual'."
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Message)
    try {
        & $Action
    } catch {
        return
    }
    throw $Message
}

$testId = [guid]::NewGuid().ToString('N')
$testRootSubKey = "Software\KGTA.Alert.Tests\$testId"
$testRunSubKey = "$testRootSubKey\Run"
$testRegistryPath = "HKCU:\$testRootSubKey"
$testExecutableDirectory = Join-Path ([IO.Path]::GetTempPath()) "KGTA Alert $testId"
$testExecutablePath = Join-Path $testExecutableDirectory 'Watcher EdgeAgent Desktop.exe'
$interactiveUserSid = "S-1-5-21-1111111111-2222222222-3333333333-$([Random]::Shared.Next(100000, 2000000000))"
$usersHive = $null
$runKey = $null

try {
    New-Item -ItemType Directory -Path $testExecutableDirectory -Force | Out-Null
    [IO.File]::WriteAllBytes($testExecutablePath, [byte[]]@())

    $currentIdentitySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Assert-Equal -Expected $currentIdentitySid -Actual (Resolve-EdgeAgentInteractiveUserSid -InteractiveUser $currentIdentitySid) -Message 'Розпізнавання SID інтерактивного користувача має бути стабільним.'
Assert-Equal -Expected $currentIdentitySid -Actual (Resolve-EdgeAgentInteractiveUserSid) -Message 'Типовий інтерактивний користувач має визначатися з поточного токена.'
Assert-Equal -Expected $true -Actual (Test-EdgeAgentInteractiveUserProfileLoaded -InteractiveUserSid $currentIdentitySid) -Message 'Профіль поточного користувача має бути завантажений.'
Assert-Throws { Resolve-EdgeAgentInteractiveUserSid -InteractiveUser "KGTA.Alert.Tests\Missing_$testId" } 'Невідомий інтерактивний користувач має відхилятися.'

$defaultRunPath = Get-EdgeAgentStartupRegistrySubKeyPath -InteractiveUserSid $interactiveUserSid
    Assert-Equal -Expected "$interactiveUserSid\Software\Microsoft\Windows\CurrentVersion\Run" -Actual $defaultRunPath -Message 'Типовий шлях має належати профілю цільового користувача.'

    $normalizedTestRunPath = Get-EdgeAgentStartupRegistrySubKeyPath -InteractiveUserSid $interactiveUserSid -RunSubKeyPath $testRunSubKey
    Assert-Equal -Expected $testRunSubKey -Actual $normalizedTestRunPath -Message 'Безпечний відносний шлях тестового підрозділу має прийматися.'
    Assert-Throws { Get-EdgeAgentStartupRegistrySubKeyPath -InteractiveUserSid $interactiveUserSid -RunSubKeyPath '..\OtherUser\Run' } 'Шлях із переходом до батьківського підрозділу має відхилятися.'
    Assert-Throws { Get-EdgeAgentStartupRegistrySubKeyPath -InteractiveUserSid $interactiveUserSid -RunSubKeyPath 'C:\Users\Public\Run' } 'Абсолютний шлях реєстру має відхилятися.'
    Assert-Throws { Get-EdgeAgentStartupRegistrySubKeyPath -InteractiveUserSid '..\S-1-5-21-1\Run' } 'Некоректний SID має відхилятися.'

    Assert-Equal -Expected 'auto' -Actual (Get-EdgeAgentServiceStartupType) -Message 'Служба має запускатися автоматично.'
    Assert-Equal -Expected 'auto' -Actual (Get-EdgeAgentServiceStartupType -SkipStart) -Message '-SkipStart має пропускати лише негайний запуск служби.'
    $installerText = Get-Content -LiteralPath $installerPath -Raw
    if ($installerText -notmatch '\$startupType\s*=\s*Get-EdgeAgentServiceStartupType\s+-SkipStart:\$SkipStart' -or
        $installerText -notmatch '&\s*sc\.exe\s+config\s+\$serviceName\s+start=\s+\$startupType') {
        throw 'Інсталятор має застосовувати автоматичний тип запуску служби незалежно від -SkipStart.'
    }

    $usersHive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::Users, [Microsoft.Win32.RegistryView]::Default)
    Assert-Equal -Expected $false -Actual (Test-EdgeAgentInteractiveUserProfileLoaded -InteractiveUserSid $interactiveUserSid) -Message 'Відсутній профіль користувача має розпізнаватися як незавантажений.'
    Assert-Throws {
        Set-EdgeAgentDesktopStartupEntry -InteractiveUserSid $interactiveUserSid -ExecutablePath $testExecutablePath
    } 'Реєстрація не повинна створювати ключ для незавантаженого профілю.'
    $missingProfile = $usersHive.OpenSubKey($interactiveUserSid, $false)
    if ($missingProfile) {
        $missingProfile.Dispose()
        throw 'Перевірка незавантаженого профілю несподівано створила підрозділ HKEY_USERS.'
    }

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $runKey = $baseKey.CreateSubKey($testRunSubKey, $true)
        if (-not $runKey) { throw 'Не вдалося створити ізольований тестовий ключ реєстру.' }
        $runKey.SetValue('ExistingStartup', '"C:\Existing\Other App.exe"', [Microsoft.Win32.RegistryValueKind]::String)
    } finally {
        if ($runKey) { $runKey.Dispose(); $runKey = $null }
        $baseKey.Dispose()
    }

    $expectedCommand = '"{0}"' -f [IO.Path]::GetFullPath($testExecutablePath)
    $firstCommand = Set-EdgeAgentDesktopStartupEntry -InteractiveUserSid $interactiveUserSid -ExecutablePath $testExecutablePath -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser) -RunSubKeyPath $testRunSubKey
    $secondCommand = Set-EdgeAgentDesktopStartupEntry -InteractiveUserSid $interactiveUserSid -ExecutablePath $testExecutablePath -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser) -RunSubKeyPath $testRunSubKey
    Assert-Equal -Expected $expectedCommand -Actual $firstCommand -Message 'Команда автозапуску має містити абсолютний шлях у лапках.'
    Assert-Equal -Expected $expectedCommand -Actual $secondCommand -Message 'Повторна реєстрація має бути ідемпотентною.'

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $runKey = $baseKey.OpenSubKey($testRunSubKey, $false)
        if (-not $runKey) { throw 'Тестовий ключ автозапуску зник після реєстрації.' }
        Assert-Equal -Expected $expectedCommand -Actual $runKey.GetValue('KGTA.Alert.OperatorPanel') -Message 'У реєстрі має зберігатися команда запуску панелі.'
        Assert-Equal -Expected ([Microsoft.Win32.RegistryValueKind]::String) -Actual $runKey.GetValueKind('KGTA.Alert.OperatorPanel') -Message 'Команда має зберігатися як REG_SZ.'
        Assert-Equal -Expected '"C:\Existing\Other App.exe"' -Actual $runKey.GetValue('ExistingStartup') -Message 'Реєстрація не має змінювати інші значення автозапуску.'
        Assert-Equal -Expected 2 -Actual $runKey.GetValueNames().Count -Message 'Реєстрація має створити лише одне власне значення.'
    } finally {
        if ($runKey) { $runKey.Dispose(); $runKey = $null }
        $baseKey.Dispose()
    }

    Remove-EdgeAgentDesktopStartupEntry -InteractiveUserSid $interactiveUserSid -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser) -RunSubKeyPath $testRunSubKey
    Remove-EdgeAgentDesktopStartupEntry -InteractiveUserSid $interactiveUserSid -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser) -RunSubKeyPath $testRunSubKey

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $runKey = $baseKey.OpenSubKey($testRunSubKey, $false)
        if (-not $runKey) { throw 'Видалення автозапуску не має видаляти ключ інших програм.' }
        Assert-Equal -Expected $false -Actual ($runKey.GetValueNames() -contains 'KGTA.Alert.OperatorPanel') -Message 'Видалення має прибирати лише власне значення.'
        Assert-Equal -Expected '"C:\Existing\Other App.exe"' -Actual $runKey.GetValue('ExistingStartup') -Message 'Видалення має зберігати чужі значення автозапуску.'
    } finally {
        if ($runKey) { $runKey.Dispose(); $runKey = $null }
        $baseKey.Dispose()
    }

    Write-Host 'Перевірки автозапуску Windows-вузла пройшли.'
} finally {
    if ($runKey) { $runKey.Dispose() }
    if ($usersHive) { $usersHive.Dispose() }
    if (Test-Path -LiteralPath $testRegistryPath) { Remove-Item -LiteralPath $testRegistryPath -Recurse -Force }
    if (Test-Path -LiteralPath $testExecutableDirectory) { Remove-Item -LiteralPath $testExecutableDirectory -Recurse -Force }
}

