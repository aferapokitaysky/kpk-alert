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

function Read-PipeBytes([System.IO.Stream]$Stream, [int]$Length) {
    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -eq 0) { throw 'Служба закрила канал до відповіді.' }
        $offset += $read
    }
    return ,$buffer
}

function Get-EdgeAgentStatus([string]$Name) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $Name, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(1000)
        $pipe.ReadTimeout = 2000
        $request = [Text.Encoding]::UTF8.GetBytes('{"schemaVersion":1,"operation":"get_status"}')
        $header = [BitConverter]::GetBytes([int]$request.Length)
        $pipe.Write($header, 0, $header.Length)
        $pipe.Write($request, 0, $request.Length)
        $pipe.Flush()
        $responseHeader = Read-PipeBytes $pipe 4
        $responseLength = [BitConverter]::ToInt32($responseHeader, 0)
        if ($responseLength -le 0 -or $responseLength -gt 4096) { throw 'Служба повернула некоректний розмір відповіді.' }
        $responseBytes = Read-PipeBytes $pipe $responseLength
        return [Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
    } finally { $pipe.Dispose() }
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
if (-not (Get-LocalGroup -Name $operatorGroupName -ErrorAction SilentlyContinue)) {
    New-LocalGroup -Name $operatorGroupName -Description 'Користувачі панелі стану KGTA Alert' | Out-Null
}
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
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
$packageDeployment = New-EdgeAgentPackageStage `
    -ServicePublishDirectory $PublishDirectory `
    -DesktopPublishDirectory $DesktopPublishDirectory `
    -InstallDirectory $installDirectory
$installDirectoryAclSnapshot = @()
$aclSnapshot = @()
$configurationExistedBeforeInstall = Test-Path -LiteralPath $installedConfiguration -PathType Leaf
$wasServiceRunning = $existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped
try {
    if ($existingService -and $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force
        $existingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
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
        $databasePath = [IO.Path]::GetFullPath([string]$configuration.EdgeAgent.DatabasePath)
        if (-not [IO.Path]::IsPathFullyQualified([string]$configuration.EdgeAgent.DatabasePath) -or
            -not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
            throw 'Для резервної копії DatabasePath має вказувати на наявний файл за абсолютним шляхом.'
        }

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

        $backupName = 'edge-agent-{0}-{1}.db' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), [guid]::NewGuid().ToString('N')
        $databaseBackupPath = Join-Path $backupDirectory $backupName
        if (Test-Path -LiteralPath $databaseBackupPath) {
            throw 'Файл резервної копії з таким позначенням уже існує; наявний файл не перезаписано.'
        }
        $backupExecutable = Join-Path $packageDeployment.StageDirectory 'Watcher.EdgeAgent.Service.exe'
        & $backupExecutable --backup-database $databasePath $databaseBackupPath
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $databaseBackupPath -PathType Leaf)) {
            throw 'Не вдалося створити узгоджену резервну копію SQLite; оновлення скасовано.'
        }
    }
    $installDirectoryAclSnapshot = Get-EdgeAgentAclSnapshot -Path @($installDirectory) -RequiredPath $(if ($packageDeployment.HadPreviousPackage) { @($installDirectory) } else { @() })
    Install-EdgeAgentPackageStage -Deployment $packageDeployment
} catch {
    $stageFailure = $_
    if (Test-Path -LiteralPath $packageDeployment.StageDirectory) {
        Remove-Item -LiteralPath $packageDeployment.StageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($wasServiceRunning) {
        try {
            if ($packageDeployment.HadPreviousPackage -and (Test-Path -LiteralPath $packageDeployment.BackupDirectory)) {
                throw 'Резервний каталог існує; попередній пакет не відновлено.'
            }
            $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($currentService -and $currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
                if ($currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                    $currentService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
                }
                & sc.exe config $serviceName start= demand | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Не вдалося тимчасово ввімкнути попередню службу.' }
                Start-Service -Name $serviceName
                (Get-Service -Name $serviceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
                & sc.exe config $serviceName start= $previousStartupType | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Не вдалося відновити тип запуску '$previousStartupType'." }
            }
        } catch {
            throw "Не вдалося перемкнути пакет, а попередню службу не вдалося запустити повторно: $_. Причина перемикання: $stageFailure"
        }
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
    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($currentService -and $currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        try { Stop-Service -Name $serviceName -Force -ErrorAction Stop } catch { $rollbackFailures.Add("Не вдалося зупинити службу: $_") }
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
            try { Start-Service -Name $serviceName -ErrorAction Stop } catch { $rollbackFailures.Add("Не вдалося запустити попередню Windows-службу: $_") }
        }
        Restore-EdgeAgentServiceStartupAfterRollback `
            -PreviousStartupType $previousStartupType `
            -RollbackFailures $rollbackFailures `
            -SetStartupType {
                param($startupType)
                & sc.exe config $serviceName start= $startupType | Out-Null
                $LASTEXITCODE -eq 0
            }
    } else {
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            & sc.exe delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0) { $rollbackFailures.Add('Не вдалося видалити створену Windows-службу.') }
        }
    }
    if ($rollbackFailures.Count -gt 0) { throw ($rollbackFailures -join ' ') }
}
if (-not $SkipStart) {
    try {
        if ($existingService -and $serviceConfiguration.StartMode -eq 'Disabled') {
            & sc.exe config $serviceName start= demand | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Не вдалося тимчасово ввімкнути службу для перевірки після оновлення.' }
        }
        Start-Service -Name $serviceName
        $healthDeadline = [DateTime]::UtcNow.AddSeconds(20)
        $statusResponse = $null
        while ([DateTime]::UtcNow -lt $healthDeadline -and -not $statusResponse) {
            try { $statusResponse = Get-EdgeAgentStatus $pipeName }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $statusResponse -or -not $statusResponse.succeeded) {
            throw 'Не вдалося отримати статус Windows-служби через Named Pipe.'
        }
        if ($statusResponse.status.connectionState -eq 'Degraded') {
            throw "Вузол перейшов у стан Degraded ($($statusResponse.status.diagnosticCode)). Перевірте права приватного ключа для $serviceAccount та журнал KGTA Alert."
        }
    } catch {
        $healthCheckFailure = $_
        $rollbackFailures = [System.Collections.Generic.List[string]]::new()
        try {
            $healthCheckService = Get-Service -Name $serviceName -ErrorAction Stop
            if ($healthCheckService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
                if ($healthCheckService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::StopPending) {
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
            } elseif ($wasServiceRunning) {
                try {
                    Start-Service -Name $serviceName
                    (Get-Service -Name $serviceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
                } catch {
                    $rollbackFailures.Add("Не вдалося повторно запустити попередню Windows-службу: $_")
                }
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
        } else {
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
        if ($rollbackFailures.Count -gt 0) {
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
    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($currentService -and $currentService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        try {
            Stop-Service -Name $serviceName -Force
            $currentService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        } catch {
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
    if ($existingService) {
        & sc.exe config $serviceName binPath= $previousServicePathName obj= $previousServiceAccount start= demand | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $rollbackFailures.Add('Не вдалося відновити шлях і обліковий запис попередньої Windows-служби.')
        } elseif ($rollbackFailures.Count -eq 0 -and $wasServiceRunning) {
            try {
                Start-Service -Name $serviceName
                (Get-Service -Name $serviceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
            } catch {
                $rollbackFailures.Add("Не вдалося повторно запустити попередню Windows-службу: $_")
            }
        }
    } else {
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { $rollbackFailures.Add('Не вдалося видалити створену під час інсталяції службу.') }
    }
    try {
        Restore-EdgeAgentOperatorShortcut -ShortcutPath $operatorShortcutPath -BackupPath $operatorShortcutBackupPath -Created $createdOperatorShortcut
    } catch {
        $rollbackFailures.Add("Не вдалося відновити ярлик оператора: $_")
    }
    if ($addedOperatorGroupMembers.Count -gt 0) {
        try {
            Remove-EdgeAgentOperatorGroupMembers `
                -Members $addedOperatorGroupMembers `
                -RemoveMember { param($member) Remove-LocalGroupMember -Group $operatorGroupName -Member $member -ErrorAction Stop }
        } catch {
            $rollbackFailures.Add("Не вдалося скасувати нове членство операторів: $_")
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
        throw "Фіналізація інсталяції не пройшла, а автоматичне відновлення має помилки: $($rollbackFailures -join ' ') Зверніться до адміністратора. Причина: $finalizationFailure"
    }
    throw "Фіналізація інсталяції не пройшла; попередній пакет і конфігурацію Windows-служби відновлено. Деталь: $finalizationFailure"
}
if ($operatorShortcutBackupPath -and (Test-Path -LiteralPath $operatorShortcutBackupPath -PathType Leaf)) {
    try { Remove-Item -LiteralPath $operatorShortcutBackupPath -Force -ErrorAction Stop }
    catch { Write-Warning ("Інсталяцію завершено, але резервну копію ярлика не прибрано: $_") }
}
$deploymentCleanup = Complete-EdgeAgentPackageDeployment -Deployment $packageDeployment
if (-not $deploymentCleanup.Succeeded) {
    Write-Warning ("Інсталяцію завершено, але не вдалося прибрати тимчасові каталоги пакета: {0}" -f ($deploymentCleanup.Failures -join '; '))
}
$finalService | Select-Object Name, Status, StartType
