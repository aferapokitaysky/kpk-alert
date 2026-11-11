Set-StrictMode -Version Latest

$script:defaultMaintenanceMutexName = 'Global\KGTA.Alert.EdgeAgent.InstallationMaintenance.v1'

function Enter-EdgeAgentInstallerMaintenanceGate {
    [CmdletBinding()]
    param(
        [string]$Name = $script:defaultMaintenanceMutexName,
        [string]$ServiceSid
    )

    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $serviceSidIdentity = if ([string]::IsNullOrWhiteSpace($ServiceSid)) {
        ([Security.Principal.NTAccount]::new('NT SERVICE', 'KGTA.Alert.EdgeAgent')).Translate(
            [Security.Principal.SecurityIdentifier]
        )
    } else {
        [Security.Principal.SecurityIdentifier]::new($ServiceSid)
    }

    $security = [System.Security.AccessControl.MutexSecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.AddAccessRule([System.Security.AccessControl.MutexAccessRule]::new(
        $systemSid,
        [System.Security.AccessControl.MutexRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow
    ))
    $security.AddAccessRule([System.Security.AccessControl.MutexAccessRule]::new(
        $administratorsSid,
        [System.Security.AccessControl.MutexRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow
    ))
    $serviceRights = [System.Security.AccessControl.MutexRights]::Synchronize -bor
        [System.Security.AccessControl.MutexRights]::Modify
    $security.AddAccessRule([System.Security.AccessControl.MutexAccessRule]::new(
        $serviceSidIdentity,
        $serviceRights,
        [System.Security.AccessControl.AccessControlType]::Allow
    ))

    $createdNew = $false
    $mutexAclType = [Type]::GetType('System.Threading.MutexAcl, System.Threading.AccessControl', $false)
    if ($mutexAclType) {
        $createArguments = [object[]]@($true, $Name, $createdNew, $security)
        $mutex = $mutexAclType.GetMethod('Create').Invoke($null, $createArguments)
        $createdNew = [bool]$createArguments[2]
    } else {
        $mutex = [System.Threading.Mutex]::new($true, $Name, [ref]$createdNew, $security)
    }
    if (-not $createdNew) {
        $mutex.Dispose()
        throw 'Інший процес уже створив mutex обслуговування; інсталяцію зупинено.'
    }

    return [pscustomobject]@{
        Mutex = $mutex
        OwnerThreadId = [System.Threading.Thread]::CurrentThread.ManagedThreadId
        Released = $false
    }
}

function Exit-EdgeAgentInstallerMaintenanceGate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Gate
    )

    if ($Gate.Released) {
        return
    }
    if ($Gate.OwnerThreadId -ne [System.Threading.Thread]::CurrentThread.ManagedThreadId) {
        throw 'Maintenance gate потрібно звільнити в тому самому потоці, який його створив.'
    }

    try {
        $Gate.Mutex.ReleaseMutex()
    } finally {
        $Gate.Mutex.Dispose()
        $Gate.Released = $true
    }
}

Export-ModuleMember -Function 'Enter-EdgeAgentInstallerMaintenanceGate',
    'Exit-EdgeAgentInstallerMaintenanceGate'
