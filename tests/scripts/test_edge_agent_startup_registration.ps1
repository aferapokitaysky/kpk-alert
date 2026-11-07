$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\EdgeAgentStartupRegistration.psm1'
$packageModulePath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\EdgeAgentPackageDeployment.psm1'
$installerPath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\install-service.ps1'
$uninstallerPath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\uninstall-service.ps1'
$userStartupScriptPath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\set-operator-panel-startup.ps1'
Import-Module -Name $modulePath -Force
Import-Module -Name $packageModulePath -Force

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
$testDeploymentRoot = Join-Path ([IO.Path]::GetTempPath()) "KGTA Alert Package Tests $testId"
$testProgramFilesPath = Join-Path ([IO.Path]::GetTempPath()) "KGTA Program Files $testId"
$random = New-Object System.Random
$interactiveUserSid = "S-1-5-21-1111111111-2222222222-3333333333-$($random.Next(100000, 2000000000))"
$actualUserSid = $null
$actualUserRunSubKey = $null
$usersHive = $null
$runKey = $null

try {
    New-Item -ItemType Directory -Path $testExecutableDirectory -Force | Out-Null
    [IO.File]::WriteAllBytes($testExecutablePath, [byte[]]@())

    $currentIdentitySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Assert-Equal -Expected $currentIdentitySid -Actual (Resolve-EdgeAgentInteractiveUserSid -InteractiveUser $currentIdentitySid) -Message 'Розпізнавання SID інтерактивного користувача має бути стабільним.'
    Assert-Equal -Expected $currentIdentitySid -Actual (Resolve-EdgeAgentInteractiveUserSid) -Message 'Типовий інтерактивний користувач має визначатися з поточного токена.'
    Assert-Equal -Expected $currentIdentitySid -Actual (Get-EdgeAgentInteractiveUserSidForContext -CurrentIdentitySid $currentIdentitySid -CurrentIdentityIsSystem $false) -Message 'Інсталяція від поточного користувача має реєструвати панель у його профілі.'
    Assert-Equal -Expected $interactiveUserSid -Actual (Get-EdgeAgentInteractiveUserSidForContext -InteractiveUser $interactiveUserSid -CurrentIdentitySid 'S-1-5-18' -CurrentIdentityIsSystem $true) -Message 'Запуск інсталятора від SYSTEM має приймати явно заданого інтерактивного користувача.'
    Assert-Throws { Get-EdgeAgentInteractiveUserSidForContext -CurrentIdentitySid 'S-1-5-18' -CurrentIdentityIsSystem $true } 'SYSTEM без явного інтерактивного користувача не має створювати запис Run у власному профілі.'
    Assert-Throws { Get-EdgeAgentInteractiveUserSidForContext -InteractiveUser 'S-1-5-18' -CurrentIdentitySid 'S-1-5-18' -CurrentIdentityIsSystem $true } 'SYSTEM не має бути цільовим інтерактивним користувачем.'
    Assert-Throws { Assert-EdgeAgentInteractiveUserSid -InteractiveUserSid 'S-1-5-19' } 'LocalService не має бути цільовим користувачем панелі.'
    Assert-Throws { Assert-EdgeAgentInteractiveUserSid -InteractiveUserSid 'S-1-5-20' } 'NetworkService не має бути цільовим користувачем панелі.'
    Assert-Throws { Assert-EdgeAgentInteractiveUserSid -InteractiveUserSid 'S-1-5-80-12345-67890-12345-67890-12345-1234' } 'Service SID не має бути цільовим користувачем панелі.'
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
    $failureFlagCommands = [regex]::Matches($installerText, '(?m)^\s*&\s*sc\.exe\s+failureflag\s+\$serviceName\s+1\s+\|\s+Out-Null\s*$')
    if ($installerText -notmatch '\$startupType\s*=\s*Get-EdgeAgentServiceStartupType\s+-SkipStart:\$SkipStart' -or
        $installerText -notmatch '&\s*sc\.exe\s+config\s+\$serviceName\s+start=\s+\$startupType' -or
        $failureFlagCommands.Count -ne 1) {
        throw 'Інсталятор має застосовувати автозапуск і відновлення після помилок служби.'
    }
    if ($installerText -notmatch 'Restore-EdgeAgentPackage\s+-Deployment\s+\$packageDeployment' -or
        $installerText -notmatch 'binPath=\s+\$previousServicePathName\s+obj=\s+\$previousServiceAccount' -or
        $installerText -notmatch 'start=\s+\$previousStartupType') {
        throw 'Невдалий health check має відновлювати попередній пакет і конфігурацію служби.'
    }
    $backupInvocation = $installerText.IndexOf('--backup-database', [StringComparison]::Ordinal)
    $packageSwitch = $installerText.IndexOf('Install-EdgeAgentPackageStage -Deployment $packageDeployment', [StringComparison]::Ordinal)
    if ($backupInvocation -lt 0 -or $packageSwitch -lt 0 -or $backupInvocation -gt $packageSwitch -or
        $installerText -notmatch 'AreAccessRulesProtected' -or $installerText -notmatch 'FileAttributes\]::ReparsePoint') {
        throw 'Перед перемиканням пакета інсталятор має створити SQLite-копію в каталозі із захищеним ACL.'
    }
    $startupRegistration = $installerText.IndexOf('Set-EdgeAgentDesktopStartupEntry', [StringComparison]::Ordinal)
    $lateRollback = $installerText.LastIndexOf('Restore-EdgeAgentPackage -Deployment $packageDeployment', [StringComparison]::Ordinal)
    $deploymentCleanup = $installerText.IndexOf('Complete-EdgeAgentPackageDeployment -Deployment $packageDeployment', [StringComparison]::Ordinal)
    if ($startupRegistration -lt 0 -or $lateRollback -lt $startupRegistration -or $deploymentCleanup -lt $lateRollback) {
        throw 'Пакет має залишатися доступним для rollback до завершення реєстрації запуску та обробки пізніх помилок.'
    }

    $serviceSource = Join-Path $testDeploymentRoot 'source-service'
    $desktopSource = Join-Path $testDeploymentRoot 'source-desktop'
    $installTarget = Join-Path $testDeploymentRoot 'Program Files\KGTA\EdgeAgent'
    $dataTarget = Join-Path $testDeploymentRoot 'ProgramData\KGTA\Alert\data\receipt.db'
    New-Item -ItemType Directory -Path $serviceSource, $desktopSource, $installTarget, (Split-Path -Parent $dataTarget) -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $serviceSource 'service.exe') -Value 'old-service'
    Set-Content -LiteralPath (Join-Path $desktopSource 'panel.exe') -Value 'old-panel'
    Set-Content -LiteralPath (Join-Path $installTarget 'service.exe') -Value 'previous-service'
    Set-Content -LiteralPath (Join-Path $installTarget 'panel.exe') -Value 'previous-panel'
    Set-Content -LiteralPath $dataTarget -Value 'preserved-receipt'
    Set-Content -LiteralPath (Join-Path $serviceSource 'service.exe') -Value 'new-service'
    Set-Content -LiteralPath (Join-Path $desktopSource 'panel.exe') -Value 'new-panel'

    $deployment = New-EdgeAgentPackageStage -ServicePublishDirectory $serviceSource -DesktopPublishDirectory $desktopSource -InstallDirectory $installTarget
    Assert-Equal -Expected 'previous-service' -Actual (Get-Content -LiteralPath (Join-Path $installTarget 'service.exe') -Raw).Trim() -Message 'Підготовка пакета не має змінювати активні бінарні файли.'
    Install-EdgeAgentPackageStage -Deployment $deployment
    Assert-Equal -Expected 'new-service' -Actual (Get-Content -LiteralPath (Join-Path $installTarget 'service.exe') -Raw).Trim() -Message 'Після перемикання має бути встановлено новий пакет.'
    Assert-Equal -Expected 'preserved-receipt' -Actual (Get-Content -LiteralPath $dataTarget -Raw).Trim() -Message 'Оновлення не має змінювати квитанції в ProgramData.'
    Restore-EdgeAgentPackage -Deployment $deployment
    Assert-Equal -Expected 'previous-service' -Actual (Get-Content -LiteralPath (Join-Path $installTarget 'service.exe') -Raw).Trim() -Message 'Після невдалого health check має відновитися попередній пакет.'
    Assert-Equal -Expected 'previous-panel' -Actual (Get-Content -LiteralPath (Join-Path $installTarget 'panel.exe') -Raw).Trim() -Message 'Rollback має відновити всі файли попереднього пакета.'
    Assert-Equal -Expected 'preserved-receipt' -Actual (Get-Content -LiteralPath $dataTarget -Raw).Trim() -Message 'Rollback не має видаляти квитанції в ProgramData.'
    Complete-EdgeAgentPackageDeployment -Deployment $deployment

    $lateFailureTarget = Join-Path $testDeploymentRoot 'Late Failure\EdgeAgent'
    New-Item -ItemType Directory -Path $lateFailureTarget -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $lateFailureTarget 'service.exe') -Value 'previous-service'
    $lateFailureDeployment = New-EdgeAgentPackageStage -ServicePublishDirectory $serviceSource -DesktopPublishDirectory $desktopSource -InstallDirectory $lateFailureTarget
    Install-EdgeAgentPackageStage -Deployment $lateFailureDeployment
    Assert-Equal -Expected $true -Actual (Test-Path -LiteralPath $lateFailureDeployment.BackupDirectory -PathType Container) -Message 'До завершення фіналізації попередній пакет має залишатися доступним для rollback.'
    try {
        throw 'simulated operator startup registration failure'
    } catch {
        Restore-EdgeAgentPackage -Deployment $lateFailureDeployment
    }
    Assert-Equal -Expected 'previous-service' -Actual (Get-Content -LiteralPath (Join-Path $lateFailureTarget 'service.exe') -Raw).Trim() -Message 'Пізня помилка до завершення інсталяції має відновити попередній пакет.'
    Assert-Equal -Expected $false -Actual (Test-Path -LiteralPath $lateFailureDeployment.BackupDirectory) -Message 'Rollback має повернути попередній пакет із тимчасового каталогу.'
    Complete-EdgeAgentPackageDeployment -Deployment $lateFailureDeployment

    $newInstallTarget = Join-Path $testDeploymentRoot 'New Install\EdgeAgent'
    $newInstall = New-EdgeAgentPackageStage -ServicePublishDirectory $serviceSource -DesktopPublishDirectory $desktopSource -InstallDirectory $newInstallTarget
    Install-EdgeAgentPackageStage -Deployment $newInstall
    Restore-EdgeAgentPackage -Deployment $newInstall
    Assert-Equal -Expected $false -Actual (Test-Path -LiteralPath $newInstallTarget) -Message 'Невдала перша інсталяція має прибрати неповний пакет.'
    Complete-EdgeAgentPackageDeployment -Deployment $newInstall
    $userStartupScriptText = Get-Content -LiteralPath $userStartupScriptPath -Raw
    if ($userStartupScriptText -notmatch 'Resolve-EdgeAgentInteractiveUserSid\s*(?:\r?\n|$)' -or
        $userStartupScriptText -notmatch 'Get-EdgeAgentInstalledDesktopExecutablePath' -or
        $userStartupScriptText -notmatch 'Set-EdgeAgentDesktopStartupEntry' -or
        $userStartupScriptText -notmatch 'Remove-EdgeAgentDesktopStartupEntry' -or
        $userStartupScriptText -match 'sc\.exe|Add-LocalGroupMember|HKLM:') {
        throw 'Команда користувача має змінювати лише Run-запис поточного профілю.'
    }

    $testDesktopDirectory = Join-Path $testProgramFilesPath 'KGTA\Alert\EdgeAgent'
    $testDesktopExecutable = Join-Path $testDesktopDirectory 'Watcher.EdgeAgent.Desktop.exe'
    New-Item -ItemType Directory -Path $testDesktopDirectory -Force | Out-Null
    [IO.File]::WriteAllBytes($testDesktopExecutable, [byte[]]@())
    Assert-Equal -Expected ([IO.Path]::GetFullPath($testDesktopExecutable)) -Actual (Get-EdgeAgentInstalledDesktopExecutablePath -ProgramFilesDirectory $testProgramFilesPath) -Message 'Команда користувача має використовувати exe лише з каталогу встановлення.'
    Assert-Throws { Get-EdgeAgentInstalledDesktopExecutablePath -ProgramFilesDirectory $testExecutableDirectory } 'Відсутня встановлена панель має відхилятися.'
    Assert-Throws { Get-EdgeAgentInstalledDesktopExecutablePath -ProgramFilesDirectory 'relative-path' } 'Відносний каталог Program Files має відхилятися.'

    $firstProfileRoot = "$testRootSubKey\FirstProfile"
    $secondProfileRoot = "$testRootSubKey\SecondProfile"
    $firstProfileRunSubKey = "$firstProfileRoot\Software\Microsoft\Windows\CurrentVersion\Run"
    $secondProfileRunSubKey = "$secondProfileRoot\Software\Microsoft\Windows\CurrentVersion\Run"
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $firstProfileRunKey = $baseKey.CreateSubKey($firstProfileRunSubKey, $true)
        $secondProfileRunKey = $baseKey.CreateSubKey($secondProfileRunSubKey, $true)
        if (-not $firstProfileRunKey -or -not $secondProfileRunKey) { throw 'Не вдалося створити ізольовані підрозділи автозапуску профілів.' }
        $expectedDesktopCommand = '"{0}"' -f [IO.Path]::GetFullPath($testExecutablePath)
        $firstProfileRunKey.SetValue('KGTA.Alert.OperatorPanel', $expectedDesktopCommand, [Microsoft.Win32.RegistryValueKind]::String)
        $firstProfileRunKey.SetValue('ExistingStartup', 'C:\Existing\Other.exe', [Microsoft.Win32.RegistryValueKind]::String)
        $secondProfileRunKey.SetValue('KGTA.Alert.OperatorPanel', '"C:\Other\Panel.exe"', [Microsoft.Win32.RegistryValueKind]::String)
        $secondProfileRunKey.SetValue('ExistingStartup', 'C:\Existing\Other.exe', [Microsoft.Win32.RegistryValueKind]::String)
        $secondProfileRunKey.SetValue('OtherPanelStartup', $expectedDesktopCommand, [Microsoft.Win32.RegistryValueKind]::String)
    } finally {
        if ($firstProfileRunKey) { $firstProfileRunKey.Dispose(); $firstProfileRunKey = $null }
        if ($secondProfileRunKey) { $secondProfileRunKey.Dispose(); $secondProfileRunKey = $null }
        $baseKey.Dispose()
    }

    $profileRecords = @(
        [pscustomobject]@{ SID = $currentIdentitySid; LocalPath = 'unused-loaded-profile'; Loaded = $true; Special = $false },
        [pscustomobject]@{ SID = $interactiveUserSid; LocalPath = 'unused-loaded-profile'; Loaded = $true; Special = $false }
    )
    $testRegistryRoots = @{}
    $testRegistryRoots[$currentIdentitySid] = $firstProfileRoot
    $testRegistryRoots[$interactiveUserSid] = $secondProfileRoot
    $profileCleanupPlan = New-EdgeAgentStartupCleanupPlan `
        -Profiles $profileRecords `
        -RequiredInteractiveUserSid $currentIdentitySid `
        -RegExecutablePath (Join-Path $env:SystemRoot 'System32\reg.exe') `
        -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser) `
        -RegistryRoots $testRegistryRoots
    Assert-Equal -Expected 2 -Actual $profileCleanupPlan.Profiles.Count -Message 'План має охопити обидва змодельовані профілі.'
    Assert-Equal -Expected 0 -Actual $profileCleanupPlan.MountedHiveNames.Count -Message 'Завантажені тестові профілі не мають монтувати hive.'
    $removedStartupEntries = @(Remove-EdgeAgentDesktopStartupEntriesForProfiles `
        -Profiles $profileCleanupPlan.Profiles `
        -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser))
    Assert-Equal -Expected 2 -Actual $removedStartupEntries.Count -Message 'Очищення має повернути snapshot кожного видаленого app-owned значення для rollback.'
    Assert-Throws {
        Remove-EdgeAgentDesktopStartupEntriesForProfiles -Profiles @() -RegistryHive ([Microsoft.Win32.RegistryHive]::LocalMachine)
    } 'Масове очищення не має приймати HKLM.'

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $firstProfileRunKey = $baseKey.OpenSubKey($firstProfileRunSubKey, $false)
        $secondProfileRunKey = $baseKey.OpenSubKey($secondProfileRunSubKey, $false)
        Assert-Equal -Expected $false -Actual ($firstProfileRunKey.GetValueNames() -contains 'KGTA.Alert.OperatorPanel') -Message 'Очищення має прибирати запис KGTA Alert у кожному профілі.'
        Assert-Equal -Expected 'C:\Existing\Other.exe' -Actual $firstProfileRunKey.GetValue('ExistingStartup') -Message 'Очищення має зберігати сторонні значення Run.'
        Assert-Equal -Expected $false -Actual ($secondProfileRunKey.GetValueNames() -contains 'KGTA.Alert.OperatorPanel') -Message 'Очищення має видалити застаріле app-owned значення навіть якщо шлях змінено.'
        Assert-Equal -Expected $expectedDesktopCommand -Actual $secondProfileRunKey.GetValue('OtherPanelStartup') -Message 'Очищення має залишити інші значення навіть якщо вони посилаються на той самий exe.'
        Assert-Equal -Expected 'C:\Existing\Other.exe' -Actual $secondProfileRunKey.GetValue('ExistingStartup') -Message 'Очищення має зберігати сторонні значення в кожному профілі.'
    } finally {
        if ($firstProfileRunKey) { $firstProfileRunKey.Dispose(); $firstProfileRunKey = $null }
        if ($secondProfileRunKey) { $secondProfileRunKey.Dispose(); $secondProfileRunKey = $null }
        $baseKey.Dispose()
    }

    Restore-EdgeAgentDesktopStartupEntriesForProfiles `
        -Entries $removedStartupEntries `
        -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser)
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $firstProfileRunKey = $baseKey.OpenSubKey($firstProfileRunSubKey, $false)
        $secondProfileRunKey = $baseKey.OpenSubKey($secondProfileRunSubKey, $false)
        Assert-Equal -Expected $expectedDesktopCommand -Actual $firstProfileRunKey.GetValue('KGTA.Alert.OperatorPanel') -Message 'Rollback має відновити Run-запис першого профілю.'
        Assert-Equal -Expected '"C:\Other\Panel.exe"' -Actual $secondProfileRunKey.GetValue('KGTA.Alert.OperatorPanel') -Message 'Rollback має відновити Run-запис другого профілю без зміни його значення.'
    } finally {
        if ($firstProfileRunKey) { $firstProfileRunKey.Dispose(); $firstProfileRunKey = $null }
        if ($secondProfileRunKey) { $secondProfileRunKey.Dispose(); $secondProfileRunKey = $null }
        $baseKey.Dispose()
    }

    $concurrentRunValue = 'C:\Concurrent\OperatorPanel.exe'
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $firstProfileRunKey = $baseKey.OpenSubKey($firstProfileRunSubKey, $true)
        $secondProfileRunKey = $baseKey.OpenSubKey($secondProfileRunSubKey, $true)
        $firstProfileRunKey.SetValue('KGTA.Alert.OperatorPanel', $concurrentRunValue, [Microsoft.Win32.RegistryValueKind]::String)
        $secondProfileRunKey.DeleteValue('KGTA.Alert.OperatorPanel', $false)
    } finally {
        if ($firstProfileRunKey) { $firstProfileRunKey.Dispose(); $firstProfileRunKey = $null }
        if ($secondProfileRunKey) { $secondProfileRunKey.Dispose(); $secondProfileRunKey = $null }
        $baseKey.Dispose()
    }
    $restoreFailureMessage = $null
    try {
        Restore-EdgeAgentDesktopStartupEntriesForProfiles `
            -Entries $removedStartupEntries `
            -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser)
    } catch {
        $restoreFailureMessage = $_.Exception.Message
    }
    if (-not $restoreFailureMessage -or -not $restoreFailureMessage.Contains($currentIdentitySid)) {
        throw 'Конфлікт відновлення має повернути помилку з SID профілю.'
    }
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $firstProfileRunKey = $baseKey.OpenSubKey($firstProfileRunSubKey, $false)
        $secondProfileRunKey = $baseKey.OpenSubKey($secondProfileRunSubKey, $false)
        Assert-Equal -Expected $concurrentRunValue -Actual $firstProfileRunKey.GetValue('KGTA.Alert.OperatorPanel') -Message 'Rollback не має перезаписувати конкурентну зміну першого профілю.'
        Assert-Equal -Expected '"C:\Other\Panel.exe"' -Actual $secondProfileRunKey.GetValue('KGTA.Alert.OperatorPanel') -Message 'Конфлікт першого профілю не має перешкоджати відновленню другого.'
        $firstProfileRunKey.Dispose()
        $firstProfileRunKey = $null
        $firstProfileRunKey = $baseKey.OpenSubKey($firstProfileRunSubKey, $true)
        $firstProfileRunKey.SetValue('KGTA.Alert.OperatorPanel', $expectedDesktopCommand, [Microsoft.Win32.RegistryValueKind]::String)
    } finally {
        if ($firstProfileRunKey) { $firstProfileRunKey.Dispose(); $firstProfileRunKey = $null }
        if ($secondProfileRunKey) { $secondProfileRunKey.Dispose(); $secondProfileRunKey = $null }
        $baseKey.Dispose()
    }

    Assert-Throws {
        Remove-EdgeAgentDesktopStartupEntriesForProfiles `
            -Profiles @(
                [pscustomobject]@{ Sid = $currentIdentitySid; RunSubKeyPath = $firstProfileRunSubKey },
                [pscustomobject]@{ Sid = $interactiveUserSid; RunSubKeyPath = '..\InvalidProfile\Run' }
            ) `
            -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser)
    } 'Помилка профілю посеред очищення має зупинити uninstall.'
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $firstProfileRunKey = $baseKey.OpenSubKey($firstProfileRunSubKey, $false)
        Assert-Equal -Expected $expectedDesktopCommand -Actual $firstProfileRunKey.GetValue('KGTA.Alert.OperatorPanel') -Message 'Помилка очищення має відновити вже видалений запис першого профілю.'
    } finally {
        if ($firstProfileRunKey) { $firstProfileRunKey.Dispose(); $firstProfileRunKey = $null }
        $baseKey.Dispose()
    }

    $fakeService = [pscustomobject]@{ Exists = $true; Status = [System.ServiceProcess.ServiceControllerStatus]::Running; RestartCount = 0; StopCount = 0 }
    $runValueBeforeFailedDelete = $null
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $firstProfileRunKey = $baseKey.OpenSubKey($firstProfileRunSubKey, $false)
        $runValueBeforeFailedDelete = $firstProfileRunKey.GetValue('KGTA.Alert.OperatorPanel')
    } finally {
        if ($firstProfileRunKey) { $firstProfileRunKey.Dispose(); $firstProfileRunKey = $null }
        $baseKey.Dispose()
    }
    Assert-Throws {
        Invoke-EdgeAgentUninstallServiceRemoval `
            -Profiles $profileCleanupPlan.Profiles `
            -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser) `
            -GetServiceAction { if ($fakeService.Exists) { [pscustomobject]@{ Status = $fakeService.Status } } } `
            -StopServiceAction { $fakeService.Status = [System.ServiceProcess.ServiceControllerStatus]::Stopped; $fakeService.StopCount++ } `
            -DeleteServiceAction { throw 'sc.exe delete failed' } `
            -StartServiceAction { $fakeService.Status = [System.ServiceProcess.ServiceControllerStatus]::Running; $fakeService.RestartCount++ }
    } 'Помилка sc delete має відновити Run-записи та перезапустити наявну службу.'
    Assert-Equal -Expected ([System.ServiceProcess.ServiceControllerStatus]::Running) -Actual $fakeService.Status -Message 'Після помилки видалення служба має працювати.'
    Assert-Equal -Expected 1 -Actual $fakeService.RestartCount -Message 'Після помилки видалення потрібна одна спроба перезапуску.'
    Assert-Equal -Expected 1 -Actual $fakeService.StopCount -Message 'До спроби видалення запущена служба має бути зупинена.'
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
    try {
        $firstProfileRunKey = $baseKey.OpenSubKey($firstProfileRunSubKey, $false)
        Assert-Equal -Expected $runValueBeforeFailedDelete -Actual $firstProfileRunKey.GetValue('KGTA.Alert.OperatorPanel') -Message 'Помилка sc delete має зберегти Run-запис.'
    } finally {
        if ($firstProfileRunKey) { $firstProfileRunKey.Dispose(); $firstProfileRunKey = $null }
        $baseKey.Dispose()
    }

    $fakeService.StopCount = 0
    $fakeService.RestartCount = 0
    Assert-Throws {
        Invoke-EdgeAgentUninstallServiceRemoval `
            -Profiles @(
                [pscustomobject]@{ Sid = $currentIdentitySid; RunSubKeyPath = $firstProfileRunSubKey },
                [pscustomobject]@{ Sid = $interactiveUserSid; RunSubKeyPath = '..\InvalidProfile\Run' }
            ) `
            -RegistryHive ([Microsoft.Win32.RegistryHive]::CurrentUser) `
            -GetServiceAction { if ($fakeService.Exists) { [pscustomobject]@{ Status = $fakeService.Status } } } `
            -StopServiceAction { $fakeService.Status = [System.ServiceProcess.ServiceControllerStatus]::Stopped; $fakeService.StopCount++ } `
            -DeleteServiceAction { $fakeService.Exists = $false } `
            -StartServiceAction { $fakeService.Status = [System.ServiceProcess.ServiceControllerStatus]::Running; $fakeService.RestartCount++ }
    } 'Помилка очищення профілю має перервати uninstall до зупинки служби.'
    Assert-Equal -Expected 0 -Actual $fakeService.StopCount -Message 'Помилка cleanup має виникнути до зупинки служби.'
    Assert-Equal -Expected 0 -Actual $fakeService.RestartCount -Message 'Служба не має перезапускатися, якщо її не зупиняли.'

    $uninstallerText = Get-Content -LiteralPath $uninstallerPath -Raw
    $cleanupModuleText = Get-Content -LiteralPath $modulePath -Raw
    $prepareProfilesPosition = $uninstallerText.IndexOf('New-EdgeAgentStartupCleanupPlan', [StringComparison]::Ordinal)
    $invokeRemovalPosition = $uninstallerText.IndexOf('Invoke-EdgeAgentUninstallServiceRemoval', [StringComparison]::Ordinal)
    $deleteServicePosition = $uninstallerText.IndexOf('& sc.exe delete $serviceName', [StringComparison]::Ordinal)
    $removeFilesPosition = $uninstallerText.IndexOf('Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force', [StringComparison]::Ordinal)
    $transactionCleanupPosition = $cleanupModuleText.IndexOf('Remove-EdgeAgentDesktopStartupEntriesForProfiles -Profiles $Profiles', [StringComparison]::Ordinal)
    $transactionDeletePosition = $cleanupModuleText.IndexOf('& $DeleteServiceAction', [StringComparison]::Ordinal)
    if ($uninstallerText -notmatch 'Get-CimInstance\s+-ClassName\s+Win32_UserProfile' -or
        $cleanupModuleText -notmatch '&\s+\$RegExecutablePath\s+load' -or
        $cleanupModuleText -notmatch '&\s+\$RegExecutablePath\s+unload' -or
        $prepareProfilesPosition -lt 0 -or $invokeRemovalPosition -le $prepareProfilesPosition -or
        $deleteServicePosition -le $invokeRemovalPosition -or $removeFilesPosition -le $deleteServicePosition -or
        $transactionCleanupPosition -lt 0 -or $transactionDeletePosition -le $transactionCleanupPosition -or
        $cleanupModuleText -notmatch 'if \(\& \$GetServiceAction\) \{ \& \$StartServiceAction \}' -or
        $cleanupModuleText -notmatch 'Restore-EdgeAgentDesktopStartupEntriesForProfiles -Entries \$removedEntries') {
        throw 'Видалення має транзакційно очистити Run, видалити службу, відновити Run і службу при помилці та лише потім прибрати файли.'
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

    $actualUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $actualUserRunSubKey = "$actualUserSid\$testRunSubKey"
    $actualHiveCommand = Set-EdgeAgentDesktopStartupEntry `
        -InteractiveUserSid $actualUserSid `
        -ExecutablePath $testExecutablePath `
        -RunSubKeyPath $actualUserRunSubKey
    Assert-Equal -Expected $expectedCommand -Actual $actualHiveCommand -Message 'Запис через HKEY_USERS цільового профілю має повертати ту саму команду.'
    $actualUserRunKey = $usersHive.OpenSubKey($actualUserRunSubKey, $false)
    try {
        if (-not $actualUserRunKey) { throw 'Запис через HKEY_USERS не створив підрозділ у профілі цільового користувача.' }
        Assert-Equal -Expected $expectedCommand -Actual $actualUserRunKey.GetValue('KGTA.Alert.OperatorPanel') -Message 'HKEY_USERS має містити команду панелі у Run цільового профілю.'
    } finally {
        if ($actualUserRunKey) { $actualUserRunKey.Dispose() }
    }
    Remove-EdgeAgentDesktopStartupEntry -InteractiveUserSid $actualUserSid -RunSubKeyPath $actualUserRunSubKey

    Write-Host 'Перевірки автозапуску Windows-вузла пройшли.'
} finally {
    if ($runKey) { $runKey.Dispose() }
    if ($usersHive -and $actualUserSid -and $actualUserRunSubKey) {
        $actualUserRunKey = $usersHive.OpenSubKey($actualUserRunSubKey, $true)
        try {
            if ($actualUserRunKey) {
                $actualUserRunKey.DeleteValue('KGTA.Alert.OperatorPanel', $false)
            }
        } finally {
            if ($actualUserRunKey) { $actualUserRunKey.Dispose() }
        }
        $usersHive.DeleteSubKeyTree("$actualUserSid\$testRootSubKey", $false)
    }
    if ($usersHive) { $usersHive.Dispose() }
    if (Test-Path -LiteralPath $testRegistryPath) { Remove-Item -LiteralPath $testRegistryPath -Recurse -Force }
    if (Test-Path -LiteralPath $testExecutableDirectory) { Remove-Item -LiteralPath $testExecutableDirectory -Recurse -Force }
    if (Test-Path -LiteralPath $testDeploymentRoot) { Remove-Item -LiteralPath $testDeploymentRoot -Recurse -Force }
    $expectedProgramFilesPath = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "KGTA Program Files $testId"))
    if ((Test-Path -LiteralPath $expectedProgramFilesPath) -and
        [IO.Path]::GetFullPath($testProgramFilesPath) -eq $expectedProgramFilesPath) {
        Remove-Item -LiteralPath $expectedProgramFilesPath -Recurse -Force
    }
}
