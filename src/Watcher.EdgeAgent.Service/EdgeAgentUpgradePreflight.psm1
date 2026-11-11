Set-StrictMode -Version Latest

if (-not ('KgtaAlert.EdgeAgent.NamedPipeIdentity' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KgtaAlert.EdgeAgent
{
    public static class NamedPipeIdentity
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        public static uint GetServerProcessId(SafePipeHandle pipe)
        {
            uint processId;
            if (!GetNamedPipeServerProcessId(pipe, out processId))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            return processId;
        }
    }
}
'@
}

function Get-EdgeAgentServiceProcessId {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='KGTA.Alert.EdgeAgent'" -ErrorAction Stop
    if (-not $service -or [uint32]$service.ProcessId -eq 0) {
        throw 'Не вдалося підтвердити процес служби KGTA.Alert.EdgeAgent.'
    }
    return [uint32]$service.ProcessId
}


function Read-EdgeAgentPipeBytes([System.IO.Stream]$Stream, [int]$Length, [int]$TimeoutMilliseconds) {
    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $timeout = [System.Threading.CancellationTokenSource]::new($TimeoutMilliseconds)
        try {
            $read = $Stream.ReadAsync($buffer, $offset, $Length - $offset, $timeout.Token).GetAwaiter().GetResult()
        } finally {
            $timeout.Dispose()
        }
        if ($read -eq 0) { throw 'Служба закрила канал до відповіді.' }
        $offset += $read
    }
    return ,$buffer
}

function Invoke-EdgeAgentPipeRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Operation,
        [Guid]$LeaseId,
        [int]$ResponseTimeoutMilliseconds = 5000
    )

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $Name, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None,
        [System.Security.Principal.TokenImpersonationLevel]::Impersonation
    )
    try {
        $pipe.Connect(1000)
        $expectedServerProcessId = Get-EdgeAgentServiceProcessId
        $actualServerProcessId = [KgtaAlert.EdgeAgent.NamedPipeIdentity]::GetServerProcessId($pipe.SafePipeHandle)
        if ($actualServerProcessId -ne $expectedServerProcessId) {
            throw 'Канал статусу не належить процесу служби KGTA.Alert.EdgeAgent.'
        }
        $request = [ordered]@{ schemaVersion = 1; operation = $Operation }
        if ($PSBoundParameters.ContainsKey('LeaseId')) { $request.leaseId = $LeaseId }
        $requestBytes = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Compress))
        $header = [BitConverter]::GetBytes([int]$requestBytes.Length)
        $pipe.Write($header, 0, $header.Length)
        $pipe.Write($requestBytes, 0, $requestBytes.Length)
        $pipe.Flush()

        $responseHeader = Read-EdgeAgentPipeBytes $pipe 4 $ResponseTimeoutMilliseconds
        $responseLength = [BitConverter]::ToInt32($responseHeader, 0)
        if ($responseLength -le 0 -or $responseLength -gt 4096) {
            throw 'Служба повернула некоректний розмір відповіді.'
        }
        $responseBytes = Read-EdgeAgentPipeBytes $pipe $responseLength $ResponseTimeoutMilliseconds
        return [Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
    } finally {
        $pipe.Dispose()
    }
}

function Begin-EdgeAgentMaintenance {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Name)

    $response = Invoke-EdgeAgentPipeRequest -Name $Name -Operation 'begin_maintenance' `
        -ResponseTimeoutMilliseconds ([int][TimeSpan]::FromMinutes(16).TotalMilliseconds)
    if ($response.diagnosticCode -eq 'invalid_request') {
        throw 'Поточна версія служби не підтримує безпечне оновлення. У погоджене вікно припиніть надсилання команд START на сервері, дочекайтеся завершення активного сигналу, зупиніть службу та повторіть установлення.'
    }
    if (-not $response.succeeded -or -not $response.leaseId) {
        throw "Служба не підтвердила безпечне обслуговування вузла. Код: $($response.diagnosticCode)"
    }
    return [guid]$response.leaseId
}

function Release-EdgeAgentMaintenance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][guid]$LeaseId
    )

    return Invoke-EdgeAgentPipeRequest -Name $Name -Operation 'release_maintenance' -LeaseId $LeaseId
}

function Backup-EdgeAgentDatabase {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][guid]$LeaseId
    )

    return Invoke-EdgeAgentPipeRequest -Name $Name -Operation 'backup_database' -LeaseId $LeaseId `
        -ResponseTimeoutMilliseconds ([int][TimeSpan]::FromMinutes(16).TotalMilliseconds)
}

function Assert-EdgeAgentDatabaseBackupResponse {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Response,
        [Parameter(Mandatory = $true)][guid]$LeaseId,
        [Parameter(Mandatory = $true)][string]$ExpectedDatabasePathHash
    )

    if (-not $Response.succeeded) {
        $diagnosticCode = if ($Response.diagnosticCode) { $Response.diagnosticCode } else { 'unknown' }
        throw "Служба не створила резервну копію бази даних. Код: $diagnosticCode"
    }
    $responseLeaseId = [guid]::Empty
    if (-not [guid]::TryParse([string]$Response.maintenanceLeaseId, [ref]$responseLeaseId) -or
        $responseLeaseId -ne $LeaseId) {
        throw 'Відповідь резервного копіювання не належить активному maintenance lease.'
    }
    if ([string]$Response.databasePathHash -cne $ExpectedDatabasePathHash) {
        throw 'Служба створила резервну копію для іншого DatabasePath.'
    }

    $backupId = [guid]::Empty
    if (-not [guid]::TryParse([string]$Response.backupId, [ref]$backupId) -or $backupId -eq [guid]::Empty) {
        throw 'Служба повернула некоректний ідентифікатор резервної копії.'
    }

    return $backupId
}

function Assert-EdgeAgentMaintenanceLeaseReleased {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Response)

    if (-not $Response.succeeded) {
        $diagnosticCode = if ($Response.diagnosticCode) { $Response.diagnosticCode } else { 'unknown' }
        throw "Служба не підтвердила звільнення maintenance lease. Код: $diagnosticCode"
    }

    return $Response
}

function Start-EdgeAgentMaintenanceWithRecovery {
    [CmdletBinding()]
    param(
        [guid]$PreviousLeaseId = [guid]::Empty,
        [Parameter(Mandatory = $true)][scriptblock]$ReleaseAction,
        [Parameter(Mandatory = $true)][scriptblock]$BeginAction
    )

    $previousReleaseFailure = $null
    if ($PreviousLeaseId -ne [guid]::Empty) {
        try {
            $previousRelease = & $ReleaseAction $PreviousLeaseId
            if (-not $previousRelease.succeeded) {
                $previousReleaseFailure = "Попередній lease не підтвердив звільнення ($($previousRelease.diagnosticCode))."
            }
        } catch {
            $previousReleaseFailure = "Не вдалося звільнити lease попереднього запуску: $_"
        }
    }

    try {
        return [guid](& $BeginAction)
    } catch {
        if ($previousReleaseFailure) {
            throw "$previousReleaseFailure Новий maintenance lease не отримано. $_"
        }
        throw
    }
}

Export-ModuleMember -Function 'Invoke-EdgeAgentPipeRequest', 'Begin-EdgeAgentMaintenance',
    'Release-EdgeAgentMaintenance', 'Backup-EdgeAgentDatabase', 'Assert-EdgeAgentDatabaseBackupResponse',
    'Assert-EdgeAgentMaintenanceLeaseReleased',
    'Start-EdgeAgentMaintenanceWithRecovery'
