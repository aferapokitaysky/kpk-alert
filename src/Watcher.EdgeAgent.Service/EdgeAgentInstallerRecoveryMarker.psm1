Set-StrictMode -Version Latest

$script:installerRecoveryMarkerPath = 'SOFTWARE\KGTA\Alert\EdgeAgent\InstallerState'
$script:installerRecoveryMarkerName = 'RecoveryRequired'
$script:installerMaintenanceMarkerName = 'MaintenanceActive'
$script:installerMaintenanceLeaseIdName = 'MaintenanceLeaseId'

function Get-EdgeAgentInstallerRecoveryState {
    [CmdletBinding()]
    param(
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::LocalMachine,
        [string]$RegistryPath = $script:installerRecoveryMarkerPath
    )

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $null
    try {
        $key = $baseKey.OpenSubKey($RegistryPath, $false)
        if (-not $key) {
            return [pscustomobject]@{ RecoveryRequired = $false; MaintenanceActive = $false; MaintenanceLeaseId = $null }
        }

        $rawLeaseId = $key.GetValue($script:installerMaintenanceLeaseIdName, $null)
        $leaseId = $null
        if ($null -ne $rawLeaseId) {
            $parsedLeaseId = [guid]::Empty
            if (-not [guid]::TryParse([string]$rawLeaseId, [ref]$parsedLeaseId) -or $parsedLeaseId -eq [guid]::Empty) {
                throw 'Реєстр містить некоректний ідентифікатор lease обслуговування.'
            }
            $leaseId = $parsedLeaseId
        }

        return [pscustomobject]@{
            RecoveryRequired = [string]$key.GetValue($script:installerRecoveryMarkerName, '') -eq '1'
            MaintenanceActive = [string]$key.GetValue($script:installerMaintenanceMarkerName, '') -eq '1'
            MaintenanceLeaseId = $leaseId
        }
    } finally {
        if ($key) { $key.Dispose() }
        $baseKey.Dispose()
    }
}

function Set-EdgeAgentInstallerMaintenanceLeaseId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][guid]$LeaseId,
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::LocalMachine,
        [string]$RegistryPath = $script:installerRecoveryMarkerPath
    )

    if ($LeaseId -eq [guid]::Empty) { throw 'Ідентифікатор lease обслуговування не може бути порожнім.' }
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $null
    try {
        $key = $baseKey.OpenSubKey($RegistryPath, $true)
        if (-not $key) { throw 'Маркер відновлення інсталятора не створено перед записом lease.' }
        $key.SetValue($script:installerMaintenanceLeaseIdName, $LeaseId.ToString('D'), [Microsoft.Win32.RegistryValueKind]::String)
        $key.Flush()
    } finally {
        if ($key) { $key.Dispose() }
        $baseKey.Dispose()
    }
}

function Clear-EdgeAgentInstallerMaintenanceLeaseId {
    [CmdletBinding()]
    param(
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::LocalMachine,
        [string]$RegistryPath = $script:installerRecoveryMarkerPath
    )

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $null
    try {
        $key = $baseKey.OpenSubKey($RegistryPath, $true)
        if ($key) {
            $key.DeleteValue($script:installerMaintenanceLeaseIdName, $false)
            $key.Flush()
        }
    } finally {
        if ($key) { $key.Dispose() }
        $baseKey.Dispose()
    }
}

function Set-EdgeAgentInstallerRecoveryMarker {
    [CmdletBinding()]
    param(
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::LocalMachine,
        [string]$RegistryPath = $script:installerRecoveryMarkerPath,
        [string]$ServiceSid
    )

    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $serviceSidIdentity = if (-not [string]::IsNullOrWhiteSpace($ServiceSid)) {
        if ($ServiceSid -match '^S-1-') {
            [Security.Principal.SecurityIdentifier]::new($ServiceSid)
        } else {
            ([Security.Principal.NTAccount]::new($ServiceSid)).Translate([Security.Principal.SecurityIdentifier])
        }
    } else {
        $serviceSidOutput = & "$env:SystemRoot\System32\sc.exe" showsid 'KGTA.Alert.EdgeAgent' 2>&1
        $serviceSidMatch = [regex]::Match(($serviceSidOutput -join [Environment]::NewLine), 'S-1-5-80-(?:\d+-){4}\d+')
        if ($LASTEXITCODE -ne 0 -or -not $serviceSidMatch.Success) {
            throw 'Не вдалося отримати service SID для захисту маркера відновлення.'
        }
        [Security.Principal.SecurityIdentifier]::new($serviceSidMatch.Value)
    }

    $security = [System.Security.AccessControl.RegistrySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new(
        $systemSid,
        [System.Security.AccessControl.RegistryRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow
    ))
    $security.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new(
        $administratorsSid,
        [System.Security.AccessControl.RegistryRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow
    ))
    $serviceRights = [System.Security.AccessControl.RegistryRights]::ReadKey
    $security.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new(
        $serviceSidIdentity,
        $serviceRights,
        [System.Security.AccessControl.AccessControlType]::Allow
    ))

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $null
    try {
        $key = $baseKey.CreateSubKey($RegistryPath, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
        if (-not $key) {
            throw 'Не вдалося створити ключ стану відновлення інсталятора.'
        }
        $key.SetAccessControl($security)
        $key.SetValue($script:installerRecoveryMarkerName, '1', [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue($script:installerMaintenanceMarkerName, '1', [Microsoft.Win32.RegistryValueKind]::String)
        $key.Flush()
    } finally {
        if ($key) { $key.Dispose() }
        $baseKey.Dispose()
    }
}

function Clear-EdgeAgentInstallerRecoveryMarker {
    [CmdletBinding()]
    param(
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::LocalMachine,
        [string]$RegistryPath = $script:installerRecoveryMarkerPath
    )

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Registry64)
    $key = $null
    try {
        $key = $baseKey.OpenSubKey($RegistryPath, $true)
        if ($key) {
            $key.DeleteValue($script:installerMaintenanceLeaseIdName, $false)
            $key.DeleteValue($script:installerMaintenanceMarkerName, $false)
            $key.DeleteValue($script:installerRecoveryMarkerName, $false)
            $key.Flush()
        }
    } finally {
        if ($key) { $key.Dispose() }
        $baseKey.Dispose()
    }
}

Export-ModuleMember -Function Set-EdgeAgentInstallerRecoveryMarker, Clear-EdgeAgentInstallerRecoveryMarker, Get-EdgeAgentInstallerRecoveryState, Set-EdgeAgentInstallerMaintenanceLeaseId, Clear-EdgeAgentInstallerMaintenanceLeaseId
