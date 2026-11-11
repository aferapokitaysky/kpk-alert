[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [Parameter(Mandatory = $true)]
    [string]$DesktopPublishDirectory,
    [string]$ConfigurationFile = "$PSScriptRoot\edge-agent.json",
    [string[]]$OperatorMembers = @(),
    [string]$InteractiveUser,
    [switch]$SkipStart
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'EdgeAgentStartupRegistration.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'EdgeAgentPackageDeployment.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'EdgeAgentUpgradePreflight.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'EdgeAgentReadiness.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'EdgeAgentInstallerMaintenanceGate.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'EdgeAgentInstallerRecoveryMarker.psm1') -Force
$serviceName = 'KGTA.Alert.EdgeAgent'
$serviceAccount = "NT SERVICE\$serviceName"
$operatorGroupName = 'KGTA Alert Operators'
$operatorGroupAce = '{0}\{1}:(OI)(CI)RX' -f $env:COMPUTERNAME, $operatorGroupName
$installDirectory = Join-Path $env:ProgramFiles 'KGTA\Alert\EdgeAgent'
$dataDirectory = Join-Path $env:ProgramData 'KGTA\Alert'
$executable = Join-Path $installDirectory 'Watcher.EdgeAgent.Service.exe'
$pipeName = 'KGTA.Alert.EdgeAgent.Status.v1'
$operatorShortcutPath = Join-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\KGTA Alert') 'Стан вузла.lnk'
$createdOperatorShortcut = $false
$operatorShortcutBackupPath = $null
$addedOperatorGroupMembers = @()
$createdOperatorGroup = $false
$createdOperatorGroupSid = $null
$installationCompleted = $false
$databaseBackupId = $null
$databaseBackupStagingPath = $null
$databaseBackupPath = $null
$packageSwitchStarted = $false
$maintenanceLeaseId = $null

function Get-EdgeAgentStatus([string]$Name) {
    return Invoke-EdgeAgentPipeRequest -Name $Name -Operation 'get_status'
}

function Start-EdgeAgentMaintenanceLease([string]$Name) {
    try {
        $recoveryState = Get-EdgeAgentInstallerRecoveryState
    } catch {
        $script:preserveInstallerMaintenanceGate = $true
        throw "Не вдалося перевірити попередній стан recovery marker; startup gate збережено. $_"
    }
    try {
        $leaseId = Start-EdgeAgentMaintenanceWithRecovery `
            -PreviousLeaseId $(if ($recoveryState.MaintenanceLeaseId) { $recoveryState.MaintenanceLeaseId } else { [guid]::Empty }) `
            -ReleaseAction { param($previousLeaseId) Release-EdgeAgentMaintenance -Name $Name -LeaseId $previousLeaseId } `
            -BeginAction { Begin-EdgeAgentMaintenance -Name $Name }
    } catch {
        if ($recoveryState.RecoveryRequired -or $recoveryState.MaintenanceLeaseId) {
            $script:preserveInstallerMaintenanceGate = $true
        }
        throw "Новий maintenance lease не отримано; маркер відновлення збережено. $_"
    }

    try {
        Set-EdgeAgentInstallerMaintenanceLeaseId -LeaseId $leaseId
    } catch {
        $script:preserveInstallerMaintenanceGate = $true
        $leaseRecoveryFailure = $null
        try {
            $release = Release-EdgeAgentMaintenance -Name $Name -LeaseId $leaseId
            Assert-EdgeAgentMaintenanceLeaseReleased -Response $release | Out-Null
        } catch { $leaseRecoveryFailure = $_ }
        $leaseRecoveryDetail = if ($leaseRecoveryFailure) { " Не вдалося звільнити щойно створений lease: $leaseRecoveryFailure" } else { '' }
        throw "Не вдалося зберегти lease у маркері відновлення; інсталятор залишає startup gate активним.$leaseRecoveryDetail Причина: $_"
    }

    return $leaseId
}

function Test-IsFullyQualifiedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }

    return $Path -match '^[A-Za-z]:[\\/]' -or $Path -match '^\\\\[^\\]+\\[^\\]+(?:\\|$)'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустіть інсталятор від імені адміністратора.'
}
$interactiveUserSid = Resolve-EdgeAgentInteractiveUserSid -InteractiveUser $InteractiveUser
if (-not (Test-EdgeAgentInteractiveUserProfileLoaded -InteractiveUserSid $interactiveUserSid)) {
    throw "Профіль користувача $interactiveUserSid не завантажено. Увійдіть у цей обліковий запис і повторіть встановлення."
}
$interactiveUserAccount = ([Security.Principal.SecurityIdentifier]::new($interactiveUserSid)).Translate(
    [Security.Principal.NTAccount]
).Value
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'Watcher.EdgeAgent.Service.exe'))) {
    throw 'У каталозі публікації немає Watcher.EdgeAgent.Service.exe.'
}
if (-not (Test-Path -LiteralPath (Join-Path $DesktopPublishDirectory 'Watcher.EdgeAgent.Desktop.exe'))) {
    throw 'У каталозі панелі немає Watcher.EdgeAgent.Desktop.exe.'
}
if (-not (Test-Path -LiteralPath $ConfigurationFile)) {
    throw 'Не знайдено конфігурацію вузла. Створіть edge-agent.json за прикладом перед установленням.'
}

$installedConfiguration = Join-Path $dataDirectory 'edge-agent.json'
$configurationToValidate = if (Test-Path -LiteralPath $installedConfiguration) { $installedConfiguration } else { $ConfigurationFile }
try {
    $configuration = Get-Content -LiteralPath $configurationToValidate -Raw | ConvertFrom-Json
} catch {
    throw 'Файл конфігурації вузла містить некоректний JSON.'
}
if (-not $configuration.EdgeAgent.BaseAddress -or -not $configuration.EdgeAgent.DatabasePath -or
    -not $configuration.EdgeAgent.AudioRootDirectory -or -not $configuration.EdgeAgent.ClientCertificateThumbprint -or
    -not $configuration.EdgeAgent.AudioEndpointId) {
    throw 'Конфігурація має містити BaseAddress, DatabasePath, AudioRootDirectory, ClientCertificateThumbprint і AudioEndpointId.'
}
$normalizedThumbprint = ($configuration.EdgeAgent.ClientCertificateThumbprint -replace '[^0-9a-fA-F]', '').ToUpperInvariant()
$certificate = Get-ChildItem -Path 'Cert:\LocalMachine\My' | Where-Object {
    $_.Thumbprint -eq $normalizedThumbprint -and $_.HasPrivateKey -and
    $_.NotBefore.ToUniversalTime() -le [DateTime]::UtcNow -and $_.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow
} | Select-Object -First 1
if (-not $certificate) {
    throw 'device_certificate_unavailable: перевірте thumbprint, чинність сертифіката та наявність приватного ключа в LocalMachine\My.'
}
$existingService = Get-EdgeAgentServiceForRollback `
    -AllowMissing $true `
    -GetServiceAction { Get-Service -Name $serviceName -ErrorAction Stop }
$previousStartupType = $null
$previousServicePathName = $null
$previousServiceAccount = $null
if ($existingService) {
    $serviceConfiguration = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
    if (-not $serviceConfiguration) { throw 'Не вдалося прочитати поточну конфігурацію Windows-служби.' }
    $previousServicePathName = $serviceConfiguration.PathName
    $previousServiceAccount = $serviceConfiguration.StartName
    if ([string]::IsNullOrWhiteSpace($previousServicePathName)) {
        throw 'Не вдалося прочитати шлях до виконуваного файла наявної Windows-служби.'
    }
    if ($previousServiceAccount -ne $serviceAccount) {
        throw "Оновлення наявної служби з обліковим записом '$previousServiceAccount' не підтримується: безпечне відновлення пароля служби неможливе."
    }
    $previousStartupType = switch ($serviceConfiguration.StartMode) {
        'Auto' { 'auto' }
        'Manual' { 'demand' }
        'Disabled' { 'disabled' }
        default { throw "Непідтримуваний тип запуску наявної служби: $($serviceConfiguration.StartMode)." }
    }
    if ($serviceConfiguration.StartMode -eq 'Auto') {
        $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
        $delayedAutoStart = (Get-ItemProperty -LiteralPath $serviceRegistryPath -Name DelayedAutoStart -ErrorAction SilentlyContinue).DelayedAutoStart
        if ($delayedAutoStart -eq 1) { $previousStartupType = 'delayed-auto' }
    }
} else {
    $previousStartupType = 'demand'
}
$installerMaintenanceGate = Enter-EdgeAgentInstallerMaintenanceGate
$preserveInstallerMaintenanceGate = $false
$installerRecoveryMarkerCreated = $false
try {
Set-EdgeAgentInstallerRecoveryMarker
$installerRecoveryMarkerCreated = $true
$packageDeployment = New-EdgeAgentPackageStage `
    -ServicePublishDirectory $PublishDirectory `
    -DesktopPublishDirectory $DesktopPublishDirectory `
    -InstallDirectory $installDirectory
$installDirectoryAclSnapshot = @()
$aclSnapshot = @()
$configurationExistedBeforeInstall = Test-Path -LiteralPath $installedConfiguration -PathType Leaf
$wasServiceRunning = $existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped
try {
    if ($wasServiceRunning) {
        try {
            $maintenanceLeaseId = Start-EdgeAgentMaintenanceLease -Name $pipeName
        } catch {
            throw "Оновлення відкладено: служба не підтвердила завершення активного відтворення й блокування нових запусків. $_"
        }

        if ($packageDeployment.HadPreviousPackage) {
            $databasePath = [IO.Path]::GetFullPath([string]$configuration.EdgeAgent.DatabasePath)
            if (-not (Test-IsFullyQualifiedPath ([string]$configuration.EdgeAgent.DatabasePath)) -or
                -not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
                throw 'Для резервної копії DatabasePath має вказувати на наявний файл за абсолютним шляхом.'
            }

            $backupStagingDirectory = Join-Path $dataDirectory 'backup-staging'
            $serviceSidOutput = & "$env:SystemRoot\System32\sc.exe" showsid $serviceName 2>&1
            $serviceSidMatch = [regex]::Match(($serviceSidOutput -join [Environment]::NewLine), 'S-1-5-80-(?:\d+-){4}\d+')
            if ($LASTEXITCODE -ne 0 -or -not $serviceSidMatch.Success) {
                throw 'Не вдалося отримати service SID для каталогу резервної копії.'
            }
            $serviceSid = [Security.Principal.SecurityIdentifier]::new($serviceSidMatch.Value)
            $backupStagingDirectory = Set-EdgeAgentDatabaseBackupStagingAcl `
                -Path $backupStagingDirectory `
                -ServiceSid $serviceSid
            $pathHasher = [Security.Cryptography.SHA256]::Create()
            try {
                $expectedDatabasePathHash = ([BitConverter]::ToString($pathHasher.ComputeHash(
                    [Text.Encoding]::UTF8.GetBytes($databasePath.ToUpperInvariant())))).Replace('-', '')
            } finally {
                $pathHasher.Dispose()
            }
            $backupResponse = Backup-EdgeAgentDatabase -Name $pipeName -LeaseId $maintenanceLeaseId
            $databaseBackupId = Assert-EdgeAgentDatabaseBackupResponse `
                -Response $backupResponse `
                -LeaseId $maintenanceLeaseId `
                -ExpectedDatabasePathHash $expectedDatabasePathHash
            $databaseBackupStagingPath = Join-Path $backupStagingDirectory "edge-agent-$($databaseBackupId.ToString('N')).db"
            $stagedDatabaseBackup = Get-Item -LiteralPath $databaseBackupStagingPath -Force -ErrorAction Stop
            if ($stagedDatabaseBackup.PSIsContainer -or
                ($stagedDatabaseBackup.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                $stagedDatabaseBackup.Length -le 0) {
                throw 'Служба не створила очікуваний звичайний файл резервної копії.'
            }
        }
    }
    if ($packageDeployment.HadPreviousPackage -and -not $wasServiceRunning) {
        throw 'Безпечне оновлення потребує запущеної попередньої служби для створення резервної копії. Запустіть службу й повторіть установлення.'
    }
    if ($existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force
        $existingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        $maintenanceLeaseId = $null
    }
    if (-not (Get-LocalGroup -Name $operatorGroupName -ErrorAction SilentlyContinue)) {
        $newOperatorGroup = New-LocalGroup -Name $operatorGroupName `
            -Description 'Користувачі панелі стану KGTA Alert' -PassThru
        $createdOperatorGroup = $true
        if (-not $newOperatorGroup -or -not $newOperatorGroup.SID) {
            throw 'Не вдалося зберегти SID щойно створеної групи операторів для безпечного rollback.'
        }
        $createdOperatorGroupSid = $newOperatorGroup.SID.Value
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $dataDirectory 'data'), (Join-Path $dataDirectory 'audio'), (Join-Path $dataDirectory 'logs') | Out-Null
    $aclSnapshot = Get-EdgeAgentAclSnapshot -Path @(
        $dataDirectory,
        $installedConfiguration,
        (Join-Path $dataDirectory 'data'),
        (Join-Path $dataDirectory 'audio'),
        (Join-Path $dataDirectory 'logs')
    ) -RequiredPath @(
        $dataDirectory,
        (Join-Path $dataDirectory 'data'),
        (Join-Path $dataDirectory 'audio'),
        (Join-Path $dataDirectory 'logs')
    )
    if ($packageDeployment.HadPreviousPackage) {
        $backupDirectory = Join-Path $dataDirectory 'backups'
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
        if ((Get-Item -LiteralPath $backupDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Каталог резервних копій не може бути посиланням на інше розташування.'
        }
        & icacls.exe $backupDirectory /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Не вдалося обмежити доступ до каталогу резервних копій.' }
        $backupAcl = Get-Acl -LiteralPath $backupDirectory
        $allowedBackupSids = @('S-1-5-32-544', 'S-1-5-18')
        $unexpectedBackupAcl = $backupAcl.Access | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowedBackupSids
        }
        if (-not $backupAcl.AreAccessRulesProtected -or $unexpectedBackupAcl) {
            throw 'ACL каталогу резервних копій не обмежений обліковими записами адміністраторів і SYSTEM.'
        }

        if (-not $databaseBackupId -or -not $databaseBackupStagingPath) {
            throw 'Служба не підготувала резервну копію до зупинки; перемикання пакета скасовано.'
        }
        $stagedDatabaseBackup = Get-Item -LiteralPath $databaseBackupStagingPath -Force -ErrorAction Stop
        if ($stagedDatabaseBackup.PSIsContainer -or
            ($stagedDatabaseBackup.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $stagedDatabaseBackup.Length -le 0) {
            throw 'Підготовлений службою файл резервної копії змінився або зник після зупинки.'
        }

        & icacls.exe $databaseBackupStagingPath /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Не вдалося обмежити доступ до резервної копії перед переміщенням.' }
        $stagedBackupAcl = Get-Acl -LiteralPath $databaseBackupStagingPath
        $unexpectedStagedBackupAcl = $stagedBackupAcl.Access | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-32-544', 'S-1-5-18')
        }
        if (-not $stagedBackupAcl.AreAccessRulesProtected -or $unexpectedStagedBackupAcl) {
            throw 'ACL резервної копії не обмежений обліковими записами адміністраторів і SYSTEM.'
        }

        $backupName = 'edge-agent-{0}-{1}.db' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $databaseBackupId.ToString('N')
        $databaseBackupPath = Join-Path $backupDirectory $backupName
        if (Test-Path -LiteralPath $databaseBackupPath) {
            throw 'Файл резервної копії з таким позначенням уже існує; наявний файл не перезаписано.'
        }
        if (-not [string]::Equals(
                [IO.Path]::GetPathRoot($databaseBackupStagingPath),
                [IO.Path]::GetPathRoot($databaseBackupPath),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Каталоги резервної копії мають бути на одному томі для атомарного переміщення.'
        }
        [IO.File]::Move($databaseBackupStagingPath, $databaseBackupPath)
        if (-not (Test-Path -LiteralPath $databaseBackupPath -PathType Leaf) -or
            (Test-Path -LiteralPath $databaseBackupStagingPath)) {
            throw 'Не вдалося атомарно перемістити підготовлений файл резервної копії.'
        }
    }
    $installDirectoryAclSnapshot = Get-EdgeAgentAclSnapshot -Path @($installDirectory) -RequiredPath $(if ($packageDeployment.HadPreviousPackage) { @($installDirectory) } else { @() })
    $packageSwitchStarted = $true
    Install-EdgeAgentPackageStage -Deployment $packageDeployment
} catch {
    $stageFailure = $_
    if ($maintenanceLeaseId) {
        try {
            $serviceAfterStopFailure = Get-EdgeAgentServiceForRollback `
                -AllowMissing $false `
                -GetServiceAction { Get-Service -Name $serviceName -ErrorAction Stop }
        } catch {
            $preserveInstallerMaintenanceGate = $true
            throw "Не вдалося перевірити стан служби після помилки підготовки пакета; startup gate утримується. $_"
        }
        if ($serviceAfterStopFailure -and $serviceAfterStopFailure.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running) {
            try {
                $releaseResponse = Release-EdgeAgentMaintenance -Name $pipeName -LeaseId $maintenanceLeaseId
                Assert-EdgeAgentMaintenanceLeaseReleased -Response $releaseResponse | Out-Null
                Clear-EdgeAgentInstallerMaintenanceLeaseId
                $maintenanceLeaseId = $null
            } catch {
                $preserveInstallerMaintenanceGate = $true
                Write-Warning "Не підтверджено звільнення maintenance lease під час rollback; маркер відновлення залишено для наступного запуску. $_"
            }
        }
    }
    if (Test-Path -LiteralPath $packageDeployment.StageDirectory) {
        Remove-Item -LiteralPath $packageDeployment.StageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($wasServiceRunning) {
        try {
            if ($packageDeployment.HadPreviousPackage -and (Test-Path -LiteralPath $packageDeployment.BackupDirectory)) {
                throw 'Резервний каталог існує; попередній пакет не відновлено.'
            }
            try {
                $currentService = Get-EdgeAgentServiceForRollback `
                    -AllowMissing $false `
                    -GetServiceAction { Get-Service -Name $serviceName -ErrorAction Stop }
            } catch {
                $preserveInstallerMaintenanceGate = $true
                throw "Не вдалося перевірити стан служби перед відновленням попереднього пакета; startup gate утримується. $_"
            }
            if ($currentService -and $currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
                if ($currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                    $currentService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
                }
                & sc.exe config $serviceName start= demand | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Не вдалося тимчасово ввімкнути попередню службу.' }
                if ($previousStartupType -ne 'disabled') {
                    & sc.exe config $serviceName start= $previousStartupType | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw "Не вдалося відновити тип запуску '$previousStartupType'." }
                }
                # Захищений маркер лишається активним до завершення rollback.
                Start-Service -Name $serviceName
                (Get-Service -Name $serviceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
                if ($previousStartupType -eq 'disabled') {
                    & sc.exe config $serviceName start= disabled | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw "Не вдалося відновити тип запуску '$previousStartupType'." }
                }
            }
        } catch {
            $restartFailure = $_
            $preserveInstallerMaintenanceGate = $true
            $recoveryFailures = [System.Collections.Generic.List[string]]::new()
            try {
                Stop-Service -Name $serviceName -Force -ErrorAction Stop
                (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
            } catch {
                $recoveryFailures.Add("Не вдалося зупинити попередню службу: $_")
            }
            if ($previousStartupType -eq 'disabled') {
                & sc.exe config $serviceName start= disabled | Out-Null
                if ($LASTEXITCODE -ne 0) { $recoveryFailures.Add('Не вдалося відновити вимкнений тип запуску служби.') }
            }
            throw "Не вдалося перемкнути пакет і запустити попередню службу: $restartFailure. Маркер відновлення залишено. $($recoveryFailures -join ' ') Причина перемикання: $stageFailure"
        }
    } elseif (-not $packageSwitchStarted) {
        throw $stageFailure
    } else {
        $preserveInstallerMaintenanceGate = $true
        throw "Не вдалося підтвердити повне відновлення служби після помилки підготовки. Маркер обслуговування залишено. Причина: $stageFailure"
    }
    throw $stageFailure
}
Invoke-EdgeAgentInstallTransaction -Action {
if (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'edge-agent.json'))) {
    Copy-Item -LiteralPath $ConfigurationFile -Destination (Join-Path $dataDirectory 'edge-agent.json')
}

if ($existingService) {
    & sc.exe config $serviceName binPath= "`"$executable`"" obj= $serviceAccount | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося оновити конфігурацію Windows-служби.' }
} else {
    & sc.exe create $serviceName binPath= "`"$executable`"" start= demand obj= $serviceAccount | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося створити Windows-службу.' }
}
& icacls.exe $installDirectory /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' $operatorGroupAce "$($serviceAccount):(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не вдалося налаштувати права каталогу програми.' }
& icacls.exe $dataDirectory /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' "$($serviceAccount):(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не вдалося налаштувати права каталогу даних.' }
& icacls.exe $installedConfiguration /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' "$($serviceAccount):R" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не вдалося налаштувати права конфігурації вузла.' }
& icacls.exe (Join-Path $dataDirectory 'data') /grant "$($serviceAccount):(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не вдалося надати службі доступ до бази даних.' }
& icacls.exe (Join-Path $dataDirectory 'audio') /grant "$($serviceAccount):(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не вдалося надати службі доступ до аудіопакетів.' }
& icacls.exe (Join-Path $dataDirectory 'logs') /grant "$($serviceAccount):(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не вдалося надати службі доступ до журналів.' }
} -Rollback {
    $rollbackFailures = [System.Collections.Generic.List[string]]::new()
    try {
        $currentService = Get-EdgeAgentServiceForRollback `
            -AllowMissing (-not [bool]$existingService) `
            -GetServiceAction { Get-Service -Name $serviceName -ErrorAction Stop }
    } catch {
        $preserveInstallerMaintenanceGate = $true
        throw "Rollback зупинено, бо не вдалося перевірити стан Windows-служби; ACL і пакет залишено без змін, startup gate утримується. $_"
    }
    if ($currentService -and $currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        try {
            Stop-EdgeAgentServiceForRollback `
                -StopAction { Stop-Service -Name $serviceName -Force -ErrorAction Stop } `
                -WaitUntilStoppedAction {
                    $serviceAfterStop = Get-Service -Name $serviceName -ErrorAction Stop
                    $serviceAfterStop.WaitForStatus(
                        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                        [TimeSpan]::FromSeconds(30)
                    )
                    if ((Get-Service -Name $serviceName -ErrorAction Stop).Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                        throw 'Служба не перейшла у стан Stopped після очікування.'
                    }
                }
        } catch {
            $preserveInstallerMaintenanceGate = $true
            throw "Rollback зупинено, щоб не змінювати ACL або пакет під час роботи служби. Startup gate залишається утриманим до ручного відновлення. $_"
        }
    }
    try { Restore-EdgeAgentAclSnapshot -Snapshot $aclSnapshot } catch { $rollbackFailures.Add("Не вдалося відновити ACL до попереднього стану: $_") }
    try {
        Remove-EdgeAgentConfigurationAfterFailedInstall `
            -ConfigurationPath $installedConfiguration `
            -ExistedBeforeInstall $configurationExistedBeforeInstall
    } catch { $rollbackFailures.Add("Не вдалося прибрати створену конфігурацію вузла: $_") }
    $packageRestored = $false
    try {
        Restore-EdgeAgentPackage -Deployment $packageDeployment
        $packageRestored = $true
    } catch {
        $rollbackFailures.Add("Не вдалося відновити попередній пакет: $_")
    }
    if ($packageRestored) {
        try { Restore-EdgeAgentAclSnapshot -Snapshot $installDirectoryAclSnapshot } catch { $rollbackFailures.Add("Не вдалося відновити ACL каталогу програми: $_") }
    }
    if ($existingService) {
        & sc.exe config $serviceName binPath= $previousServicePathName obj= $previousServiceAccount start= demand | Out-Null
        if ($LASTEXITCODE -ne 0) { $rollbackFailures.Add('Не вдалося відновити конфігурацію попередньої Windows-служби.') }
        if ($wasServiceRunning -and $rollbackFailures.Count -eq 0) {
            try {
                Start-Service -Name $serviceName -ErrorAction Stop
                (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))            } catch {
                $preserveInstallerMaintenanceGate = $true
                try {
                    Stop-Service -Name $serviceName -Force -ErrorAction Stop
                    (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
                } catch {
                    $rollbackFailures.Add("Не вдалося зупинити службу після невдалого запуску попереднього пакета: $_")
                }
                $rollbackFailures.Add("Не вдалося запустити попередню Windows-службу: $_")
            }
        }
    } else {
        if ($currentService) {
            & sc.exe delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0) { $rollbackFailures.Add('Не вдалося видалити створену Windows-службу.') }
        }
    }
    if ($existingService) {
        Restore-EdgeAgentServiceStartupAfterRollback `
            -PreviousStartupType $previousStartupType `
            -RollbackFailures $rollbackFailures `
            -SetStartupType {
                param($startupType)
                & sc.exe config $serviceName start= $startupType | Out-Null
                $LASTEXITCODE -eq 0
            }
    }
    if ($rollbackFailures.Count -gt 0) {
        $preserveInstallerMaintenanceGate = $true
        throw ($rollbackFailures -join ' ')
    }
}
if (-not $SkipStart) {
    try {
        if ($existingService -and $serviceConfiguration.StartMode -eq 'Disabled') {
            & sc.exe config $serviceName start= demand | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Не вдалося тимчасово ввімкнути службу для перевірки після оновлення.' }
        }
        Start-Service -Name $serviceName
        # Новий процес служби має власний координатор; блокуємо START до кінця перевірки й фіналізації.
        $maintenanceLeaseId = Start-EdgeAgentMaintenanceLease -Name $pipeName
        $healthDeadline = [DateTime]::UtcNow.AddSeconds(30)
        $statusResponse = $null
        $agentReady = $false
        $agentDegraded = $false
        $lastPipeErrorType = $null
        while ([DateTime]::UtcNow -lt $healthDeadline) {
            try {
                $statusResponse = Get-EdgeAgentStatus $pipeName
                if ($statusResponse.status.connectionState -eq 'Degraded') {
                    $agentDegraded = $true
                    break
                }
                if (Test-EdgeAgentReadiness -Response $statusResponse) {
                    $agentReady = $true
                    break
                }
            } catch {
                $lastPipeErrorType = $_.Exception.GetType().Name
            }
            Start-Sleep -Milliseconds 500
        }
        if ($agentDegraded) {
            throw "Вузол перейшов у стан Degraded ($($statusResponse.status.diagnosticCode)). Перевірте права приватного ключа для $serviceAccount та журнал KGTA Alert."
        }
        if (-not $agentReady) {
            $observedState = if ($statusResponse -and $statusResponse.status) {
                "connection=$($statusResponse.status.connectionState), audio=$($statusResponse.status.audioState), diagnostic=$($statusResponse.status.diagnosticCode)"
            } else {
                'статус через Named Pipe недоступний'
            }
            $pipeError = if ($lastPipeErrorType) { "; тип останньої помилки каналу: $lastPipeErrorType" } else { '' }
            throw "Вузол не підтвердив готовність за 30 секунд: потрібні перевірена identity, активне з'єднання mTLS/WSS і доступний налаштований аудіовихід; спостережено: $observedState$pipeError."
        }
    } catch {
        $healthCheckFailure = $_
        $rollbackFailures = [System.Collections.Generic.List[string]]::new()
        try {
            $healthCheckService = Get-EdgeAgentServiceForRollback `
                -AllowMissing $false `
                -GetServiceAction { Get-Service -Name $serviceName -ErrorAction Stop }
            if ($healthCheckService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                if ($healthCheckService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::StopPending) {
                    # Міжпроцесний gate не дозволяє іншому процесу запустити службу під час rollback.
                    Stop-Service -Name $serviceName -Force -ErrorAction Stop
                }
            }
            $healthCheckService.WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(30))
            $healthCheckService.Refresh()
            if ($healthCheckService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                throw 'Windows не підтвердила зупинку служби.'
            }
            $serviceStopped = $true
        } catch {
            $serviceStopped = $false
            $preserveInstallerMaintenanceGate = $true
            $rollbackFailures.Add("Не вдалося підтвердити зупинку Windows-служби перед rollback: $_")
        }

        if ($serviceStopped) {
            try { Restore-EdgeAgentAclSnapshot -Snapshot $aclSnapshot } catch { $rollbackFailures.Add("Не вдалося відновити ACL до попереднього стану: $_") }
            try {
                Remove-EdgeAgentConfigurationAfterFailedInstall `
                    -ConfigurationPath $installedConfiguration `
                    -ExistedBeforeInstall $configurationExistedBeforeInstall
            } catch { $rollbackFailures.Add("Не вдалося прибрати створену конфігурацію вузла: $_") }
            $packageRestored = $false
            try {
                Restore-EdgeAgentPackage -Deployment $packageDeployment
                $packageRestored = $true
            } catch {
                $rollbackFailures.Add("Не вдалося відновити попередній пакет: $_")
            }
            if ($packageRestored) {
                try { Restore-EdgeAgentAclSnapshot -Snapshot $installDirectoryAclSnapshot } catch { $rollbackFailures.Add("Не вдалося відновити ACL каталогу програми: $_") }
            }
        }
        if ($existingService -and $rollbackFailures.Count -eq 0) {
            & sc.exe config $serviceName binPath= $previousServicePathName obj= $previousServiceAccount start= demand | Out-Null
            if ($LASTEXITCODE -ne 0) {
                $rollbackFailures.Add('Не вдалося відновити шлях і обліковий запис попередньої Windows-служби.')
            }
            if ($rollbackFailures.Count -eq 0 -and $wasServiceRunning) {
                try {
                    # Маркер лишається активним, доки rollback і запуск служби не підтверджені.
                    Start-Service -Name $serviceName
                    (Get-Service -Name $serviceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
                } catch {
                    $preserveInstallerMaintenanceGate = $true
                    try {
                        Stop-Service -Name $serviceName -Force -ErrorAction Stop
                        (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
                    } catch {
                        $rollbackFailures.Add("Не вдалося зупинити службу після невдалого запуску попереднього пакета: $_")
                    }
                    $rollbackFailures.Add("Не вдалося повторно запустити попередню Windows-службу: $_")
                }
            }
        } elseif (-not $existingService) {
            if ($serviceStopped) {
                & sc.exe delete $serviceName | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    $rollbackFailures.Add('Не вдалося видалити створену під час невдалої інсталяції службу.')
                    & sc.exe config $serviceName start= disabled | Out-Null
                    if ($LASTEXITCODE -ne 0) { $rollbackFailures.Add('Не вдалося вимкнути автозапуск служби після невдалого rollback.') }
                }
            } else {
                & sc.exe config $serviceName start= disabled | Out-Null
                if ($LASTEXITCODE -ne 0) { $rollbackFailures.Add('Не вдалося вимкнути автозапуск служби після невдалого rollback.') }
            }
        }
        if ($existingService) {
            Restore-EdgeAgentServiceStartupAfterRollback `
                -PreviousStartupType $previousStartupType `
                -RollbackFailures $rollbackFailures `
                -SetStartupType {
                    param($startupType)
                    & sc.exe config $serviceName start= $startupType | Out-Null
                    $LASTEXITCODE -eq 0
                }
        }
        if ($rollbackFailures.Count -gt 0) {
            $preserveInstallerMaintenanceGate = $true
            throw "Перевірка служби не пройшла. $($rollbackFailures -join ' ') Зверніться до адміністратора. Причина: $healthCheckFailure"
        }
        throw "edge_agent_health_check_failed: попередній пакет і конфігурацію служби відновлено. Перевірте журнал Application у Windows Event Viewer. Для device_certificate_unavailable відкрийте certlm.msc → керування приватними ключами сертифіката → надайте $serviceAccount право Read, а потім повторіть інсталяцію. Деталь: $healthCheckFailure"
    }
}
try {
    & sc.exe sidtype $serviceName restricted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося обмежити service SID.' }
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося налаштувати відновлення служби.' }
    & sc.exe failureflag $serviceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося ввімкнути відновлення служби після помилки фонового процесу.' }

    $startMenu = Split-Path -Parent $operatorShortcutPath
    New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
    $operatorShortcutBackupPath = Backup-EdgeAgentOperatorShortcut -ShortcutPath $operatorShortcutPath
    $createdOperatorShortcut = -not $operatorShortcutBackupPath
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($operatorShortcutPath)
    $shortcut.TargetPath = Join-Path $installDirectory 'Watcher.EdgeAgent.Desktop.exe'
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.Save()
    $membersToAdd = @($OperatorMembers) + @($interactiveUserAccount)
    $addedOperatorGroupMembers = Add-EdgeAgentOperatorGroupMembers `
        -Members $membersToAdd `
        -GetMembers {
            Get-LocalGroupMember -Group $operatorGroupName -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $_.Name
                    if ($_.Name.StartsWith("$env:COMPUTERNAME\", [StringComparison]::OrdinalIgnoreCase)) {
                        $_.Name.Substring($env:COMPUTERNAME.Length + 1)
                    }
                }
        } `
        -AddMember { param($member) Add-LocalGroupMember -Group $operatorGroupName -Member $member } `
        -RemoveMember { param($member) Remove-LocalGroupMember -Group $operatorGroupName -Member $member -ErrorAction Stop }
    if (-not (Get-LocalGroupMember -Group $operatorGroupName -ErrorAction SilentlyContinue |
            Where-Object { $_.SID.Value -eq $interactiveUserSid })) {
        throw 'Не вдалося підтвердити членство інтерактивного користувача в групі операторів.'
    }

    $startupType = Get-EdgeAgentServiceStartupType -SkipStart:$SkipStart
    & sc.exe config $serviceName start= $startupType | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося налаштувати автоматичний запуск Windows-служби.' }
    $configuredService = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
    if (-not $configuredService -or $configuredService.StartMode -ne 'Auto') {
        throw 'Windows не підтвердила автоматичний тип запуску служби.'
    }

    if ($SkipStart) {
        Write-Warning "Службу налаштовано на автоматичний запуск після завантаження Windows, але зараз не запущено й не перевірено. Надайте $serviceAccount право читання приватного ключа в certlm.msc, перевірте запуск командою Start-Service -Name '$serviceName' і стан автозапуску через Get-Service -Name '$serviceName'."
    }
    $finalService = Get-Service -Name $serviceName
    Set-EdgeAgentDesktopStartupEntry `
        -InteractiveUserSid $interactiveUserSid `
        -ExecutablePath (Join-Path $installDirectory 'Watcher.EdgeAgent.Desktop.exe')
} catch {
    $finalizationFailure = $_
    $rollbackFailures = [System.Collections.Generic.List[string]]::new()
    try {
        $currentService = Get-EdgeAgentServiceForRollback `
            -AllowMissing $false `
            -GetServiceAction { Get-Service -Name $serviceName -ErrorAction Stop }
    } catch {
        $preserveInstallerMaintenanceGate = $true
        $rollbackFailures.Add("Не вдалося перевірити стан служби перед фінальним rollback: $_")
    }
    if ($currentService -and $currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        try {
            # Gate залишається утриманим і після перезапуску служби до завершення відновлення пакета.
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            $currentService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
            if ((Get-Service -Name $serviceName -ErrorAction Stop).Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                throw 'Служба повторно запустилася перед відновленням попереднього пакета.'
            }
        } catch {
            $preserveInstallerMaintenanceGate = $true
            $rollbackFailures.Add("Не вдалося зупинити службу перед відновленням пакета: $_")
        }
    }

    $packageRestored = $false
    if ($rollbackFailures.Count -eq 0) {
        try { Restore-EdgeAgentAclSnapshot -Snapshot $aclSnapshot } catch { $rollbackFailures.Add("Не вдалося відновити ACL до попереднього стану: $_") }
    }
    if ($rollbackFailures.Count -eq 0) {
        try {
            Remove-EdgeAgentConfigurationAfterFailedInstall `
                -ConfigurationPath $installedConfiguration `
                -ExistedBeforeInstall $configurationExistedBeforeInstall
        } catch { $rollbackFailures.Add("Не вдалося прибрати створену конфігурацію вузла: $_") }
    }
    if ($rollbackFailures.Count -eq 0) {
        try {
            Restore-EdgeAgentPackage -Deployment $packageDeployment
            $packageRestored = $true
        } catch {
            $rollbackFailures.Add("Не вдалося відновити попередній пакет: $_")
        }
    }
    if ($packageRestored) {
        try { Restore-EdgeAgentAclSnapshot -Snapshot $installDirectoryAclSnapshot } catch { $rollbackFailures.Add("Не вдалося відновити ACL каталогу програми: $_") }
    }
    if ($rollbackFailures.Count -eq 0 -and $existingService) {
        & sc.exe config $serviceName binPath= $previousServicePathName obj= $previousServiceAccount start= demand | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $rollbackFailures.Add('Не вдалося відновити шлях і обліковий запис попередньої Windows-служби.')
        }
    } elseif (-not $existingService) {
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { $rollbackFailures.Add('Не вдалося видалити створену під час інсталяції службу.') }
    }
    if ($rollbackFailures.Count -eq 0) {
        try {
            Restore-EdgeAgentOperatorShortcut -ShortcutPath $operatorShortcutPath -BackupPath $operatorShortcutBackupPath -Created $createdOperatorShortcut
        } catch {
            $rollbackFailures.Add("Не вдалося відновити ярлик оператора: $_")
        }
    }
    if ($rollbackFailures.Count -eq 0 -and $addedOperatorGroupMembers.Count -gt 0) {
        try {
            Remove-EdgeAgentOperatorGroupMembers `
                -Members $addedOperatorGroupMembers `
                -RemoveMember { param($member) Remove-LocalGroupMember -Group $operatorGroupName -Member $member -ErrorAction Stop }
        } catch {
            $rollbackFailures.Add("Не вдалося скасувати нове членство операторів: $_")
        }
    }
    if ($rollbackFailures.Count -eq 0 -and $existingService -and $wasServiceRunning) {
        try {
            # Маркер лишається активним до підтвердженого запуску попередньої служби.
            Start-Service -Name $serviceName
            (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
        } catch {
            $preserveInstallerMaintenanceGate = $true
            try {
                Stop-Service -Name $serviceName -Force -ErrorAction Stop
                (Get-Service -Name $serviceName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
            } catch {
                $rollbackFailures.Add("Не вдалося зупинити службу після невдалого запуску попереднього пакета: $_")
            }
            $rollbackFailures.Add("Не вдалося повторно запустити попередню Windows-службу: $_")
        }
    }
    if ($existingService) {
        Restore-EdgeAgentServiceStartupAfterRollback `
            -PreviousStartupType $previousStartupType `
            -RollbackFailures $rollbackFailures `
            -SetStartupType {
                param($startupType)
                & sc.exe config $serviceName start= $startupType | Out-Null
                $LASTEXITCODE -eq 0
            }
    }
    if ($rollbackFailures.Count -gt 0) {
        $preserveInstallerMaintenanceGate = $true
        throw "Фіналізація інсталяції не пройшла, а автоматичне відновлення має помилки: $($rollbackFailures -join ' ') Зверніться до адміністратора. Причина: $finalizationFailure"
    }
    throw "Фіналізація інсталяції не пройшла; попередній пакет і конфігурацію Windows-служби відновлено. Деталь: $finalizationFailure"
}
if ($maintenanceLeaseId) {
    try {
        $releaseResponse = Release-EdgeAgentMaintenance -Name $pipeName -LeaseId $maintenanceLeaseId
        Assert-EdgeAgentMaintenanceLeaseReleased -Response $releaseResponse | Out-Null
        Clear-EdgeAgentInstallerMaintenanceLeaseId
        $maintenanceLeaseId = $null
    } catch {
        $preserveInstallerMaintenanceGate = $true
        throw "Інсталяцію не можна позначити завершеною: maintenance lease не звільнено або його стан невідомий. Маркер відновлення залишено. $_"
    }
}
if ($operatorShortcutBackupPath -and (Test-Path -LiteralPath $operatorShortcutBackupPath -PathType Leaf)) {
    try { Remove-Item -LiteralPath $operatorShortcutBackupPath -Force -ErrorAction Stop }
    catch { Write-Warning ("Інсталяцію завершено, але резервну копію ярлика не прибрано: $_") }
}
$deploymentCleanup = Complete-EdgeAgentPackageDeployment -Deployment $packageDeployment
if (-not $deploymentCleanup.Succeeded) {
    Write-Warning ("Інсталяцію завершено, але не вдалося прибрати тимчасові каталоги пакета: {0}" -f ($deploymentCleanup.Failures -join '; '))
}
$installationCompleted = $true
$finalService | Select-Object Name, Status, StartType
} finally {
    if (-not $installationCompleted -and -not $existingService -and $createdOperatorGroup) {
        try {
            Remove-EdgeAgentOperatorGroupIfEmpty `
                -Name $operatorGroupName `
                -ExpectedSid $createdOperatorGroupSid `
                -GetGroup { param($name) Get-LocalGroup -Name $name -ErrorAction Stop } `
                -GetMembers { param($name) Get-LocalGroupMember -Group $name -ErrorAction Stop } `
                -RemoveGroup { param($name) Remove-LocalGroup -Name $name -ErrorAction Stop } | Out-Null
        } catch {
            $preserveInstallerMaintenanceGate = $true
            throw "Не вдалося прибрати нову порожню групу операторів після невдалої першої інсталяції; маркер відновлення залишено. $_"
        }
    }
    if (-not $preserveInstallerMaintenanceGate) {
        if ($installerRecoveryMarkerCreated) {
            try {
                Clear-EdgeAgentInstallerRecoveryMarker
            } catch {
                $preserveInstallerMaintenanceGate = $true
                throw "Не вдалося очистити маркер відновлення інсталятора; служба залишиться заблокованою до ручного відновлення. $_"
            }
        }
    }
    if (-not $preserveInstallerMaintenanceGate) {
        Exit-EdgeAgentInstallerMaintenanceGate -Gate $installerMaintenanceGate
    }
}
