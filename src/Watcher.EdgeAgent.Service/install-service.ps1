[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [Parameter(Mandatory = $true)]
    [string]$DesktopPublishDirectory,
    [string]$ConfigurationFile = "$PSScriptRoot\edge-agent.json",
    [string[]]$OperatorMembers = @(),
    [switch]$SkipStart
)

$ErrorActionPreference = 'Stop'
$serviceName = 'KGTA.Alert.EdgeAgent'
$serviceAccount = "NT SERVICE\$serviceName"
$operatorGroupName = 'KGTA Alert Operators'
$operatorGroupAce = '{0}\{1}:(OI)(CI)RX' -f $env:COMPUTERNAME, $operatorGroupName
$installDirectory = Join-Path $env:ProgramFiles 'KGTA\Alert\EdgeAgent'
$dataDirectory = Join-Path $env:ProgramData 'KGTA\Alert'
$executable = Join-Path $installDirectory 'Watcher.EdgeAgent.Service.exe'
$pipeName = 'KGTA.Alert.EdgeAgent.Status.v1'

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
foreach ($member in $OperatorMembers) {
    $alreadyMember = Get-LocalGroupMember -Group $operatorGroupName -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $member -or $_.Name -eq "$env:COMPUTERNAME\$member" }
    if (-not $alreadyMember) {
        Add-LocalGroupMember -Group $operatorGroupName -Member $member
    }
}

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    if ($existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force
        $existingService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }
}

New-Item -ItemType Directory -Force -Path $installDirectory, (Join-Path $dataDirectory 'data'), (Join-Path $dataDirectory 'audio'), (Join-Path $dataDirectory 'logs') | Out-Null
Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $installDirectory -Recurse -Force
Copy-Item -Path (Join-Path $DesktopPublishDirectory '*') -Destination $installDirectory -Recurse -Force
if (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'edge-agent.json'))) {
    Copy-Item -LiteralPath $ConfigurationFile -Destination (Join-Path $dataDirectory 'edge-agent.json')
}

if ($existingService) {
    & sc.exe config $serviceName binPath= "`"$executable`"" start= demand obj= $serviceAccount | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося оновити конфігурацію Windows-служби.' }
} else {
    & sc.exe create $serviceName binPath= "`"$executable`"" start= demand obj= $serviceAccount | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Не вдалося створити Windows-службу.' }
}
& sc.exe sidtype $serviceName restricted | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не вдалося обмежити service SID.' }

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
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не вдалося налаштувати відновлення служби.' }
if (-not $SkipStart) {
    try {
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
        if ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        }
        throw "edge_agent_health_check_failed: перевірте журнал Application у Windows Event Viewer. Для device_certificate_unavailable відкрийте certlm.msc → керування приватними ключами сертифіката → надайте $serviceAccount право Read, а потім повторіть інсталяцію. Деталь: $_"
    }
    & sc.exe config $serviceName start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Служба перевірена, але не вдалося увімкнути автоматичний запуск.' }
}
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\KGTA Alert'
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startMenu 'Стан вузла.lnk'))
$shortcut.TargetPath = Join-Path $installDirectory 'Watcher.EdgeAgent.Desktop.exe'
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Save()
if ($OperatorMembers.Count -eq 0) {
    $currentMember = "$env:USERDOMAIN\$env:USERNAME"
    Write-Warning "Група операторів порожня. Щоб відкрити панель поточному адміністратору, виконайте: Add-LocalGroupMember -Group 'KGTA Alert Operators' -Member '$currentMember'"
}
if ($SkipStart) {
    Write-Warning "Службу встановлено з запуском вручну. Надайте $serviceAccount право читання приватного ключа в certlm.msc, перевірте її через Start-Service -Name '$serviceName', потім виконайте: Set-Service -Name '$serviceName' -StartupType Automatic"
}
Get-Service -Name $serviceName | Select-Object Name, Status, StartType
