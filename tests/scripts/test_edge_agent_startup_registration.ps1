$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\EdgeAgentStartupRegistration.psm1'
$packageModulePath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\EdgeAgentPackageDeployment.psm1'
$installerPath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\install-service.ps1'
$uninstallerPath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\uninstall-service.ps1'
$userStartupScriptPath = Join-Path $PSScriptRoot '..\..\src\Watcher.EdgeAgent.Service\set-operator-panel-startup.ps1'
$installerTokens = $null
$installerParseErrors = $null
$installerAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $installerPath),
    [ref]$installerTokens,
    [ref]$installerParseErrors)
if ($installerParseErrors.Count -gt 0) {
    $parseSummary = ($installerParseErrors | ForEach-Object {
        "Рядок $($_.Extent.StartLineNumber): $($_.Message)"
    }) -join [Environment]::NewLine
    throw "Сценарій інсталятора містить синтаксичні помилки:`n$parseSummary"
}
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

    $startupTypeAttempts = [System.Collections.Generic.List[string]]::new()
    $incompleteRollbackFailures = [System.Collections.Generic.List[string]]::new()
    $incompleteRollbackFailures.Add('Не вдалося підтвердити зупинку Windows-служби перед rollback.')
    Restore-EdgeAgentServiceStartupAfterRollback `
        -PreviousStartupType 'auto' `
        -RollbackFailures $incompleteRollbackFailures `
        -SetStartupType { param($startupType) $startupTypeAttempts.Add($startupType); $true }
    Assert-Equal -Expected 1 -Actual $startupTypeAttempts.Count -Message 'Помилка зупинки служби має встановити лише безпечний тип запуску.'
    Assert-Equal -Expected 'disabled' -Actual $startupTypeAttempts[0] -Message 'Після непідтвердженої зупинки служба має залишитися вимкненою.'

    $startupTypeAttempts.Clear()
    $completeRollbackFailures = [System.Collections.Generic.List[string]]::new()
    Restore-EdgeAgentServiceStartupAfterRollback `
        -PreviousStartupType 'auto' `
        -RollbackFailures $completeRollbackFailures `
        -SetStartupType { param($startupType) $startupTypeAttempts.Add($startupType); $true }
    Assert-Equal -Expected 1 -Actual $startupTypeAttempts.Count -Message 'Успішний rollback має відновити тип запуску одним викликом.'
    Assert-Equal -Expected 'auto' -Actual $startupTypeAttempts[0] -Message 'Після успішного rollback має відновитися попередній тип запуску.'

    $startupTypeAttempts.Clear()
    $startupRestoreFailures = [System.Collections.Generic.List[string]]::new()
    Restore-EdgeAgentServiceStartupAfterRollback `
        -PreviousStartupType 'auto' `
        -RollbackFailures $startupRestoreFailures `
        -SetStartupType {
            param($startupType)
            $startupTypeAttempts.Add($startupType)
            $startupType -eq 'disabled'
        }
    Assert-Equal -Expected 2 -Actual $startupTypeAttempts.Count -Message 'Невдале відновлення попереднього типу має викликати fail-closed fallback.'
    Assert-Equal -Expected 'disabled' -Actual $startupTypeAttempts[1] -Message 'Fallback має вимкнути автозапуск.'
    Assert-Equal -Expected $true -Actual ($startupRestoreFailures.Count -gt 0) -Message 'Невдалу спробу відновлення попереднього типу треба зафіксувати.'

    $simulatedInstall = [pscustomobject]@{ ServiceCreated = $false; PackageSwitched = $false; RolledBack = $false }
    Assert-Throws {
        Invoke-EdgeAgentInstallTransaction -Action {
            $simulatedInstall.ServiceCreated = $true
            $simulatedInstall.PackageSwitched = $true
            throw 'simulated ACL failure after sc.exe create'
        } -Rollback {
            $simulatedInstall.ServiceCreated = $false
            $simulatedInstall.PackageSwitched = $false
            $simulatedInstall.RolledBack = $true
        }
    } 'Помилка ACL після створення служби має поширювати початкову помилку.'
    Assert-Equal -Expected $true -Actual $simulatedInstall.RolledBack -Message 'Помилка ACL після sc.exe create має виконати rollback.'
    Assert-Equal -Expected $false -Actual $simulatedInstall.ServiceCreated -Message 'Rollback після помилки ACL має видалити створену службу.'
    Assert-Equal -Expected $false -Actual $simulatedInstall.PackageSwitched -Message 'Rollback після помилки ACL має відновити попередній пакет.'

    $saveFailureState = [pscustomobject]@{ PackageSwitched = $false; ServiceRunning = $false }
    Assert-Throws {
        Invoke-EdgeAgentInstallTransaction -Action {
            Get-EdgeAgentAclSnapshot -Path @($testExecutableDirectory) -RequiredPath @((Join-Path $testExecutableDirectory 'missing-required-acl-path'))
            $saveFailureState.PackageSwitched = $true
        } -Rollback {
            $saveFailureState.PackageSwitched = $false
            $saveFailureState.ServiceRunning = $true
        }
    } 'Помилка збереження обовязкового ACL до перемикання пакета має передаватися.'
    Assert-Equal -Expected $false -Actual $saveFailureState.PackageSwitched -Message 'Помилка snapshot до Install-EdgeAgentPackageStage не має перемикати пакет.'
    Assert-Equal -Expected $true -Actual $saveFailureState.ServiceRunning -Message 'Ранній збій snapshot має відновити попередню запущену службу.'

    $shortcutTestPath = Join-Path ([IO.Path]::GetTempPath()) "KGTA shortcut rollback $testId.lnk"
    Set-Content -LiteralPath $shortcutTestPath -Value 'previous shortcut target'
    $shortcutBackup = Backup-EdgeAgentOperatorShortcut -ShortcutPath $shortcutTestPath
    Set-Content -LiteralPath $shortcutTestPath -Value 'replacement shortcut target'
    Restore-EdgeAgentOperatorShortcut -ShortcutPath $shortcutTestPath -BackupPath $shortcutBackup -Created $false
    Assert-Equal -Expected 'previous shortcut target' -Actual (Get-Content -LiteralPath $shortcutTestPath -Raw).Trim() -Message 'Rollback має відновити раніше наявний ярлик оператора.'
    Assert-Equal -Expected $false -Actual (Test-Path -LiteralPath $shortcutBackup) -Message 'Відновлення має перемістити резервну копію на початкове місце.'
    Remove-Item -LiteralPath $shortcutTestPath -Force
    Restore-EdgeAgentOperatorShortcut -ShortcutPath $shortcutTestPath -BackupPath $null -Created $true
    Assert-Equal -Expected $false -Actual (Test-Path -LiteralPath $shortcutTestPath) -Message 'Rollback має прибрати ярлик, створений невдалою першою інсталяцією.'

    $aclTestPath = Join-Path ([IO.Path]::GetTempPath()) "KGTA ACL rollback $testId"
    $aclChildPath = Join-Path $aclTestPath 'child'
    $installAclTestPath = Join-Path ([IO.Path]::GetTempPath()) "KGTA install ACL rollback $testId"
    New-Item -ItemType Directory -Path $aclTestPath, $aclChildPath, $installAclTestPath -Force | Out-Null
    try {
        $originalSddlByPath = @{}
        $ignoredProtectedDacl = $false
        $originalSddlByPath[$aclTestPath] = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddl($aclTestPath, [ref]$ignoredProtectedDacl)
        $originalSddlByPath[$aclChildPath] = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddl($aclChildPath, [ref]$ignoredProtectedDacl)
        $originalSddlByPath[$installAclTestPath] = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddl($installAclTestPath, [ref]$ignoredProtectedDacl)
        $aclSnapshot = Get-EdgeAgentAclSnapshot -Path @($aclTestPath, $aclChildPath, $installAclTestPath) -RequiredPath @($aclTestPath, $aclChildPath, $installAclTestPath)
        Assert-Throws {
            Invoke-EdgeAgentInstallTransaction -Action {
                & icacls.exe $aclTestPath /inheritance:r /grant:r '*S-1-5-32-545:(OI)(CI)RX' /q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not simulate first ACL update' }
                & icacls.exe $aclChildPath /grant '*S-1-5-11:(OI)(CI)M' /q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not simulate child ACL update' }
                & icacls.exe $installAclTestPath /grant '*S-1-5-11:(OI)(CI)M' /q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not simulate partial ACL update' }
                throw 'simulated icacls failure after partially changing the ACL set'
            } -Rollback {
                Restore-EdgeAgentAclSnapshot -Snapshot $aclSnapshot
            }
        } 'Помилка після часткової зміни ACL має передаватися після rollback.'
        $actualSddl = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddl($aclTestPath, [ref]$ignoredProtectedDacl)
        Assert-Equal -Expected $originalSddlByPath[$aclTestPath] -Actual $actualSddl -Message 'Rollback має відновити SDDL кореневого каталогу після partial failure.'
        $actualSddl = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddl($aclChildPath, [ref]$ignoredProtectedDacl)
        Assert-Equal -Expected $originalSddlByPath[$aclChildPath] -Actual $actualSddl -Message 'Rollback має відновити SDDL дочірнього каталогу після partial failure.'
        $actualSddl = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddl($installAclTestPath, [ref]$ignoredProtectedDacl)
        Assert-Equal -Expected $originalSddlByPath[$installAclTestPath] -Actual $actualSddl -Message 'Rollback має відновити SDDL каталогу програми після partial failure.'
    } finally {
        Remove-Item -LiteralPath $aclTestPath, $installAclTestPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    $backupAclParent = Join-Path ([IO.Path]::GetTempPath()) "KGTA backup ACL parent $testId"
    $backupAclDirectory = Join-Path $backupAclParent 'backups'
    $databaseBackupFile = Join-Path $backupAclDirectory 'edge-agent-test.db'
    New-Item -ItemType Directory -Path $backupAclParent, $backupAclDirectory -Force | Out-Null
    $backupWriter = [IO.File]::Open($databaseBackupFile, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $backupHandles = @()
    try {
        $parentAclSnapshot = Get-EdgeAgentAclSnapshot -Path @($backupAclParent) -RequiredPath @($backupAclParent)
        $backupHandles += [pscustomobject]@{ Path = $backupAclParent; Handle = [Kgta.Alert.Security.NativeAcl]::OpenAclHandle($backupAclParent) }
        $backupHandles += [pscustomobject]@{ Path = $backupAclDirectory; Handle = [Kgta.Alert.Security.NativeAcl]::OpenAclHandle($backupAclDirectory) }
        $backupHandles += [pscustomobject]@{ Path = $databaseBackupFile; Handle = [Kgta.Alert.Security.NativeAcl]::OpenAclHandle($databaseBackupFile) }
        $originalParentSddl = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddlFromHandle($backupHandles[0].Handle, [ref]$ignoredProtectedDacl)
        Assert-Throws {
            Invoke-EdgeAgentInstallTransaction -Action {
                & icacls.exe $backupAclDirectory /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' /q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not secure backup directory' }
                $backupBytes = [Text.Encoding]::UTF8.GetBytes('emergency database backup')
                $backupWriter.Write($backupBytes, 0, $backupBytes.Length)
                $backupWriter.Flush()
                throw 'simulated install failure after backup creation'
            } -Rollback {
                Restore-EdgeAgentAclSnapshot -Snapshot $parentAclSnapshot
            }
        } 'Помилка інсталяції після створення резервної копії має передаватися.'
        $restoredParentSddl = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddlFromHandle($backupHandles[0].Handle, [ref]$ignoredProtectedDacl)
        Assert-Equal -Expected $originalParentSddl -Actual $restoredParentSddl -Message 'Rollback має відновити DACL каталогу даних, не відкочуючи ACL резервної копії.'
        foreach ($backupHandle in @($backupHandles[1], $backupHandles[2])) {
            $backupProtectedDacl = $false
            $backupSddl = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddlFromHandle($backupHandle.Handle, [ref]$backupProtectedDacl)
            $allowedSidMatches = [regex]::Matches($backupSddl, '\(A;[^;]*;[^;]*;[^;]*;[^;]*;([^)]+)\)')
            $allowedSids = @($allowedSidMatches | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
            $requiresProtectedDacl = $backupHandle.Path -eq $backupAclDirectory
            if (($requiresProtectedDacl -and -not $backupProtectedDacl) -or
                @($allowedSids | Where-Object { $_ -notin @('S-1-5-32-544', 'S-1-5-18', 'BA', 'SY') }).Count -gt 0 -or $allowedSids.Count -eq 0) {
                throw "Після rollback DACL аварійної копії має бути обмежений Administrators/SYSTEM: $($backupHandle.Path); protected=$backupProtectedDacl; allowed=$($allowedSids -join ',')"
            }
        }
    } finally {
        foreach ($backupHandle in $backupHandles) {
            [Kgta.Alert.Security.NativeAcl]::CloseAclHandle($backupHandle.Handle)
        }
        $backupWriter.Dispose()
        Remove-Item -LiteralPath $backupAclParent -Recurse -Force -ErrorAction SilentlyContinue
    }

    $installerText = Get-Content -LiteralPath $installerPath -Raw
    $startupRollbackCalls = [regex]::Matches(
        $installerText,
        'Restore-EdgeAgentServiceStartupAfterRollback\s+`\s*\r?\n\s*-PreviousStartupType\s+\$previousStartupType')
    $stageRecoveryStart = $installerText.IndexOf('& sc.exe config $serviceName start= $previousStartupType', [StringComparison]::Ordinal)
    $transactionRollbackStart = $installerText.IndexOf('Restore-EdgeAgentServiceStartupAfterRollback', [StringComparison]::Ordinal)
    if ($startupRollbackCalls.Count -ne 3 -or $stageRecoveryStart -lt 0 -or
        $stageRecoveryStart -gt $transactionRollbackStart) {
        throw 'Усі три rollback-гілки мають відновлювати тип запуску лише через fail-closed перевірку.'
    }
    $finalizationRollbackStart = $installerText.IndexOf('$finalizationFailure = $_', [StringComparison]::Ordinal)
    $finalizationRollbackEnd = $installerText.IndexOf('throw "Фіналізація інсталяції не пройшла;', $finalizationRollbackStart, [StringComparison]::Ordinal)
    $finalizationRollbackText = $installerText.Substring(
        $finalizationRollbackStart,
        $finalizationRollbackEnd - $finalizationRollbackStart)
    $shortcutRollbackPosition = $finalizationRollbackText.IndexOf('Restore-EdgeAgentOperatorShortcut', [StringComparison]::Ordinal)
    $operatorRollbackPosition = $finalizationRollbackText.IndexOf('Remove-EdgeAgentOperatorGroupMembers', [StringComparison]::Ordinal)
    $startupRollbackPosition = $finalizationRollbackText.IndexOf('Restore-EdgeAgentServiceStartupAfterRollback', [StringComparison]::Ordinal)
    $rollbackFailureCheckPosition = $finalizationRollbackText.IndexOf('if ($rollbackFailures.Count -gt 0)', [StringComparison]::Ordinal)
    if ($shortcutRollbackPosition -lt 0 -or $operatorRollbackPosition -lt $shortcutRollbackPosition -or
        $startupRollbackPosition -lt $operatorRollbackPosition -or $rollbackFailureCheckPosition -lt $startupRollbackPosition) {
        throw 'Фінальний тип запуску можна відновлювати лише після rollback пакета, ярлика й членства операторів.'
    }
    $rollbackConfigurationPath = Join-Path ([IO.Path]::GetTempPath()) "KGTA config rollback $testId.json"
    try {
        Set-Content -LiteralPath $rollbackConfigurationPath -Value 'configuration created by first install'
        Remove-EdgeAgentConfigurationAfterFailedInstall `
            -ConfigurationPath $rollbackConfigurationPath `
            -ExistedBeforeInstall $false
        Assert-Equal -Expected $false -Actual (Test-Path -LiteralPath $rollbackConfigurationPath) -Message 'Health-check rollback після першої інсталяції має прибрати створену конфігурацію.'

        Set-Content -LiteralPath $rollbackConfigurationPath -Value 'configuration from previous install'
        Remove-EdgeAgentConfigurationAfterFailedInstall `
            -ConfigurationPath $rollbackConfigurationPath `
            -ExistedBeforeInstall $true
        Assert-Equal -Expected 'configuration from previous install' -Actual (Get-Content -LiteralPath $rollbackConfigurationPath -Raw).Trim() -Message 'Health-check rollback оновлення має зберегти попередню конфігурацію.'
    } finally {
        Remove-Item -LiteralPath $rollbackConfigurationPath -Force -ErrorAction SilentlyContinue
    }

    $healthRollbackStart = $installerText.IndexOf('$healthCheckFailure = $_', [StringComparison]::Ordinal)
    if ($healthRollbackStart -lt 0) {
        throw 'Health-check rollback має прибирати лише конфігурацію, створену під час першої інсталяції.'
    }
    $healthRollbackEnd = $installerText.IndexOf('edge_agent_health_check_failed:', $healthRollbackStart, [StringComparison]::Ordinal)
    if ($healthRollbackEnd -lt $healthRollbackStart -or
        -not $installerText.Substring($healthRollbackStart, $healthRollbackEnd - $healthRollbackStart).Contains('Remove-EdgeAgentConfigurationAfterFailedInstall')) {
        throw 'Health-check rollback має прибирати лише конфігурацію, створену під час першої інсталяції.'
    }
    $healthRollbackText = $installerText.Substring($healthRollbackStart, $healthRollbackEnd - $healthRollbackStart)
    if ($healthRollbackText -notmatch '(?s)Stop-Service\s+-Name\s+\$serviceName\s+-Force\s+-ErrorAction\s+Stop.*?WaitForStatus\s*\(\s*\[System\.ServiceProcess\.ServiceControllerStatus\]::Stopped\s*,\s*\[TimeSpan\]::FromSeconds\(30\)\s*\).*?\$serviceStopped\s*=\s*\$true') {
        throw 'Health-check rollback має вимагати підтвердженої зупинки служби з обмеженим часом очікування.'
    }
    if ($healthRollbackText -notmatch '(?s)catch\s*\{\s*\$serviceStopped\s*=\s*\$false\s*\$rollbackFailures\.Add\("Не вдалося підтвердити зупинку Windows-служби перед rollback:') {
        throw 'Помилка Stop-Service або WaitForStatus має блокувати rollback файлів і конфігурації.'
    }
    $existingServiceRestore = $healthRollbackText.IndexOf('if ($existingService -and $rollbackFailures.Count -eq 0)', [StringComparison]::Ordinal)
    $startupRollback = $healthRollbackText.IndexOf('Restore-EdgeAgentServiceStartupAfterRollback', [StringComparison]::Ordinal)
    $serviceStoppedRollbackBlocks = @($installerAst.FindAll({
        param($node)
        if ($node -isnot [System.Management.Automation.Language.IfStatementAst]) { return $false }
        foreach ($clause in $node.Clauses) {
            if ($clause.Item1.Extent.Text.Trim() -eq '$serviceStopped' -and
                $clause.Item2.Extent.StartOffset -ge $healthRollbackStart -and
                $clause.Item2.Extent.EndOffset -le $healthRollbackEnd) { return $true }
        }
        return $false
    }, $true) | ForEach-Object {
        foreach ($clause in $_.Clauses) {
            if ($clause.Item1.Extent.Text.Trim() -eq '$serviceStopped') { $clause.Item2 }
        }
    } | Where-Object { $_.Extent.Text.Contains('Restore-EdgeAgentAclSnapshot -Snapshot $aclSnapshot') })
    $serviceStoppedRollbackBody = if ($serviceStoppedRollbackBlocks.Count -gt 0) { $serviceStoppedRollbackBlocks[0].Extent.Text } else { '' }
    if ($serviceStoppedRollbackBlocks.Count -ne 1 -or
        -not $serviceStoppedRollbackBody.Contains('Restore-EdgeAgentAclSnapshot -Snapshot $aclSnapshot') -or
        -not $serviceStoppedRollbackBody.Contains('Remove-EdgeAgentConfigurationAfterFailedInstall') -or
        -not $serviceStoppedRollbackBody.Contains('Restore-EdgeAgentPackage -Deployment $packageDeployment') -or
        $existingServiceRestore -lt 0 -or
        $startupRollback -lt $existingServiceRestore -or
        $healthRollbackText -notmatch '(?s)if\s*\(\$existingService\)\s*\{\s*Restore-EdgeAgentServiceStartupAfterRollback(?:\s|\x60)*-PreviousStartupType(?:\s|\x60)+\$previousStartupType(?:\s|\x60)+-RollbackFailures(?:\s|\x60)+\$rollbackFailures' -or
        $healthRollbackText -notmatch '(?s)if\s*\(\$serviceStopped\)\s*\{.*?\}\s*else\s*\{\s*&\s*sc\.exe\s+config\s+\$serviceName\s+start=\s+disabled') {
        throw 'Health-check rollback має змінювати ACL, конфігурацію й пакет лише після підтвердження зупинки служби, а за помилки вимикати автозапуск.'
    }
    $finalizationRollbackStart = $installerText.IndexOf('$finalizationFailure = $_', [StringComparison]::Ordinal)
    if ($finalizationRollbackStart -lt 0) {
        throw 'Відкат після фіналізації має прибирати лише конфігурацію, створену під час першої інсталяції.'
    }
    $finalizationRollbackEnd = $installerText.IndexOf('if ($rollbackFailures.Count -gt 0)', $finalizationRollbackStart, [StringComparison]::Ordinal)
    $finalizationRollbackText = if ($finalizationRollbackEnd -ge $finalizationRollbackStart) {
        $installerText.Substring($finalizationRollbackStart, $finalizationRollbackEnd - $finalizationRollbackStart)
    } else {
        ''
    }
    $finalizationServiceStopPosition = $finalizationRollbackText.IndexOf(
        '$currentService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped',
        [StringComparison]::Ordinal)
    $finalizationConfigurationCleanupPosition = $finalizationRollbackText.IndexOf(
        'Remove-EdgeAgentConfigurationAfterFailedInstall',
        [StringComparison]::Ordinal)
    $finalizationConfigurationPathPosition = -1
    if ($finalizationConfigurationCleanupPosition -ge 0) {
        $finalizationConfigurationPathPosition = $finalizationRollbackText.IndexOf(
            '-ConfigurationPath $installedConfiguration',
            $finalizationConfigurationCleanupPosition,
            [StringComparison]::Ordinal)
    }
    $finalizationConfigurationExistedPosition = -1
    if ($finalizationConfigurationPathPosition -ge 0) {
        $finalizationConfigurationExistedPosition = $finalizationRollbackText.IndexOf(
            '-ExistedBeforeInstall $configurationExistedBeforeInstall',
            $finalizationConfigurationPathPosition,
            [StringComparison]::Ordinal)
    }
    $finalizationPackageRestorePosition = $finalizationRollbackText.IndexOf(
        'Restore-EdgeAgentPackage -Deployment $packageDeployment',
        [StringComparison]::Ordinal)
    if ($finalizationRollbackEnd -lt $finalizationRollbackStart -or
        $finalizationServiceStopPosition -lt 0 -or
        $finalizationConfigurationCleanupPosition -le $finalizationServiceStopPosition -or
        $finalizationConfigurationPathPosition -le $finalizationConfigurationCleanupPosition -or
        $finalizationConfigurationExistedPosition -le $finalizationConfigurationPathPosition -or
        $finalizationPackageRestorePosition -le $finalizationConfigurationExistedPosition) {
        throw 'Відкат після фіналізації має прибирати лише конфігурацію, створену під час першої інсталяції.'
    }
    if ($installerText -match '\$aclSnapshot\s*\+=\s*Get-EdgeAgentAclSnapshot\s+-Path\s+@\(\$backupDirectory\)') {
        throw 'Закритий каталог резервної копії не має повертатися до попереднього ACL під час rollback.'
    }
    $dataSnapshotPosition = $installerText.IndexOf('$aclSnapshot = Get-EdgeAgentAclSnapshot', [StringComparison]::Ordinal)
    $installSnapshotPosition = $installerText.IndexOf('$installDirectoryAclSnapshot = Get-EdgeAgentAclSnapshot', [StringComparison]::Ordinal)
    $packageStagePosition = $installerText.IndexOf('Install-EdgeAgentPackageStage -Deployment $packageDeployment', [StringComparison]::Ordinal)
    $installTransactionPosition = $installerText.IndexOf('Invoke-EdgeAgentInstallTransaction -Action', [StringComparison]::Ordinal)
    $dataAclRollbackPosition = $installerText.IndexOf('Restore-EdgeAgentAclSnapshot -Snapshot $aclSnapshot', [StringComparison]::Ordinal)
    $installAclRollbackPosition = $installerText.IndexOf('Restore-EdgeAgentAclSnapshot -Snapshot $installDirectoryAclSnapshot', [StringComparison]::Ordinal)
    if ($dataSnapshotPosition -lt 0 -or $installSnapshotPosition -lt $dataSnapshotPosition -or
        $packageStagePosition -lt $installSnapshotPosition -or $installTransactionPosition -lt $packageStagePosition -or
        $dataAclRollbackPosition -lt $installTransactionPosition -or $installAclRollbackPosition -lt $dataAclRollbackPosition) {
        throw 'До зміни ACL потрібно зберегти їхній стан і відновлювати його в транзакційному rollback.'
    }
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
    if ($installerText -notmatch '\$createdOperatorShortcut\s*=\s*\$false' -or
        $installerText -notmatch '\$addedOperatorGroupMembers\s*=\s*@\(\)' -or
        $installerText -notmatch '(?s)Backup-EdgeAgentOperatorShortcut\s+-ShortcutPath\s+\$operatorShortcutPath.*?\$shortcut\.Save\(\)' -or
        $installerText -notmatch '(?s)Add-EdgeAgentOperatorGroupMembers\s+`\s*\r?\n\s*-Members\s+\$membersToAdd') {
        throw 'Інсталятор має відстежувати лише створені ним ярлик і членство оператора.'
    }
    $hasShortcutRollback = $installerText.Contains('Restore-EdgeAgentOperatorShortcut')
    $hasOperatorMembershipRollback = $installerText.Contains('Remove-EdgeAgentOperatorGroupMembers')
    if (-not $hasShortcutRollback -or -not $hasOperatorMembershipRollback) {
        throw 'Rollback першої інсталяції має прибирати власні артефакти лише після невдалої першої інсталяції.'
    }

    $groupMembers = [System.Collections.Generic.List[string]]::new()
    $groupMembers.Add('COMPUTER\ExistingOperator')
    $addedGroupMembers = Add-EdgeAgentOperatorGroupMembers `
        -Members @('COMPUTER\ExistingOperator', 'COMPUTER\NewOperator') `
        -GetMembers { $groupMembers.ToArray() } `
        -AddMember { param($member) $groupMembers.Add($member) } `
        -RemoveMember { param($member) $groupMembers.Remove($member) | Out-Null }
    Assert-Equal -Expected 1 -Actual $addedGroupMembers.Count -Message 'Оновлення має відстежити лише нове членство.'
    Assert-Equal -Expected 'COMPUTER\NewOperator' -Actual $addedGroupMembers[0] -Message 'Раніше відсутній оператор має потрапити до rollback-списку.'
    Remove-EdgeAgentOperatorGroupMembers `
        -Members $addedGroupMembers `
        -RemoveMember { param($member) $groupMembers.Remove($member) | Out-Null }
    Assert-Equal -Expected 1 -Actual $groupMembers.Count -Message 'Rollback невдалого upgrade має видалити нового оператора.'
    Assert-Equal -Expected 'COMPUTER\ExistingOperator' -Actual $groupMembers[0] -Message 'Rollback має зберегти наявного члена групи.'

    Assert-Throws {
        Add-EdgeAgentOperatorGroupMembers `
            -Members @('COMPUTER\PartiallyAdded', 'COMPUTER\Rejected') `
            -GetMembers { $groupMembers.ToArray() } `
            -AddMember {
                param($member)
                if ($member -eq 'COMPUTER\Rejected') { throw 'simulated group update failure' }
                $groupMembers.Add($member)
            } `
            -RemoveMember { param($member) $groupMembers.Remove($member) | Out-Null }
    } 'Помилка серед групових змін має відкотити частково додане членство.'
    Assert-Equal -Expected 1 -Actual $groupMembers.Count -Message 'Частковий збій не має залишати нового члена групи.'
    if (-not $installerText.Contains('$deploymentCleanup = Complete-EdgeAgentPackageDeployment') -or -not $installerText.Contains('Write-Warning')) {
        throw 'Помилка прибирання тимчасових файлів не має подаватися як невдала інсталяція після успішної перевірки.'
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

    $cleanupFailureDirectory = Join-Path $testDeploymentRoot 'Cleanup Failure'
    New-Item -ItemType Directory -Path $cleanupFailureDirectory -Force | Out-Null
    $lockedFilePath = Join-Path $cleanupFailureDirectory 'locked-file.txt'
    Set-Content -LiteralPath $lockedFilePath -Value 'тимчасовий файл'
    $lockedFile = [IO.File]::Open($lockedFilePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $cleanupResult = Complete-EdgeAgentPackageDeployment -Deployment ([pscustomobject]@{
            StageDirectory = $cleanupFailureDirectory
            BackupDirectory = Join-Path $testDeploymentRoot 'missing-backup'
        })
        Assert-Equal -Expected $false -Actual $cleanupResult.Succeeded -Message 'Помилка видалення зайнятого тимчасового файлу має бути зафіксована.'
        Assert-Equal -Expected 1 -Actual $cleanupResult.Failures.Count -Message 'Результат очищення має містити причину невдалого видалення.'
        Assert-Equal -Expected $true -Actual (Test-Path -LiteralPath $lockedFilePath) -Message 'Помилка очищення не має видаляти чи приховувати активні дані.'
    } finally {
        $lockedFile.Dispose()
        Remove-Item -LiteralPath $cleanupFailureDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

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
