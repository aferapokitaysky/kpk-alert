Set-StrictMode -Version Latest

$script:EdgeAgentStartupValueName = 'KGTA.Alert.OperatorPanel'
$script:EdgeAgentRunSubKeySuffix = 'Software\Microsoft\Windows\CurrentVersion\Run'

function Assert-EdgeAgentInteractiveUserSid {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$InteractiveUserSid)

    try {
        $sid = [Security.Principal.SecurityIdentifier]::new($InteractiveUserSid)
    } catch [System.ArgumentException] {
        throw 'SID інтерактивного користувача має некоректний формат.'
    }

    if ($sid.IsWellKnown([Security.Principal.WellKnownSidType]::LocalSystemSid) -or
        $sid.IsWellKnown([Security.Principal.WellKnownSidType]::LocalServiceSid) -or
        $sid.IsWellKnown([Security.Principal.WellKnownSidType]::NetworkServiceSid) -or
        $sid.Value -match '^S-1-5-(?:80|82|83)-') {
        throw 'Для автозапуску панелі вкажіть інтерактивний обліковий запис, а не системний або службовий SID.'
    }

    return $sid.Value
}

function Get-EdgeAgentInteractiveUserSidForContext {
    [CmdletBinding()]
    param(
        [string]$InteractiveUser,
        [string]$CurrentIdentitySid,
        [bool]$CurrentIdentityIsSystem
    )

    if ([string]::IsNullOrWhiteSpace($InteractiveUser)) {
        if ($CurrentIdentityIsSystem) {
            throw 'Під час встановлення від імені SYSTEM потрібно явно вказати -InteractiveUser для інтерактивного облікового запису.'
        }
        if ([string]::IsNullOrWhiteSpace($CurrentIdentitySid)) {
            throw 'Не вдалося визначити SID поточного облікового запису.'
        }
        $sid = [Security.Principal.SecurityIdentifier]::new($CurrentIdentitySid)
    } elseif ($InteractiveUser -match '^S-\d-(?:\d+-)*\d+$') {
        $sid = [Security.Principal.SecurityIdentifier]::new($InteractiveUser)
    } else {
        try {
            $sid = ([Security.Principal.NTAccount]::new($InteractiveUser)).Translate(
                [Security.Principal.SecurityIdentifier]
            )
        } catch [System.Security.Principal.IdentityNotMappedException] {
            throw "Не вдалося визначити SID інтерактивного користувача '$InteractiveUser'."
        }
    }

    if (-not $sid) { throw 'Не вдалося визначити SID інтерактивного користувача.' }

    return Assert-EdgeAgentInteractiveUserSid -InteractiveUserSid $sid.Value
}

function Resolve-EdgeAgentInteractiveUserSid {
    [CmdletBinding()]
    param([string]$InteractiveUser)

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        return Get-EdgeAgentInteractiveUserSidForContext -InteractiveUser $InteractiveUser -CurrentIdentitySid $identity.User.Value -CurrentIdentityIsSystem $identity.IsSystem
    } finally {
        $identity.Dispose()
    }
}

function Get-EdgeAgentStartupRegistrySubKeyPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InteractiveUserSid,
        [string]$RunSubKeyPath
    )

    $InteractiveUserSid = Assert-EdgeAgentInteractiveUserSid -InteractiveUserSid $InteractiveUserSid

    if ([string]::IsNullOrWhiteSpace($RunSubKeyPath)) {
        return "$InteractiveUserSid\$script:EdgeAgentRunSubKeySuffix"
    }

    $segments = $RunSubKeyPath -split '[\\/]'
    $invalidSegments = @($segments | Where-Object {
        [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..' -or $_.Contains(':')
    })
    if ([IO.Path]::IsPathRooted($RunSubKeyPath) -or $segments.Count -eq 0 -or $invalidSegments.Count -gt 0) {
        throw 'Шлях підрозділу реєстру має бути відносним і не містити навігації до батьківського каталогу.'
    }

    return ($segments -join '\')
}

function Test-EdgeAgentInteractiveUserProfileLoaded {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$InteractiveUserSid)

    $InteractiveUserSid = Assert-EdgeAgentInteractiveUserSid -InteractiveUserSid $InteractiveUserSid

    $usersHive = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::Users,
        [Microsoft.Win32.RegistryView]::Default
    )
    try {
        $profileKey = $usersHive.OpenSubKey($InteractiveUserSid, $false)
        if (-not $profileKey) { return $false }
        $profileKey.Dispose()
        return $true
    } finally {
        $usersHive.Dispose()
    }
}

function Get-EdgeAgentServiceStartupType {
    [CmdletBinding()]
    param([switch]$SkipStart)

    # -SkipStart пропускає лише негайний запуск і перевірку служби, а не її автозапуск після перезавантаження.
    return 'auto'
}

function Set-EdgeAgentDesktopStartupEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InteractiveUserSid,
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::Users,
        [string]$RunSubKeyPath
    )

    $canonicalExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
    if (-not [IO.Path]::IsPathRooted($canonicalExecutablePath) -or
        -not (Test-Path -LiteralPath $canonicalExecutablePath -PathType Leaf)) {
        throw 'Не знайдено виконуваний файл панелі за абсолютним шляхом.'
    }

    $subKeyPath = Get-EdgeAgentStartupRegistrySubKeyPath `
        -InteractiveUserSid $InteractiveUserSid `
        -RunSubKeyPath $RunSubKeyPath
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Default)
    $runKey = $null
    $profileKey = $null

    try {
        if ($RegistryHive -eq [Microsoft.Win32.RegistryHive]::Users) {
            $profileKey = $baseKey.OpenSubKey($InteractiveUserSid, $false)
            if (-not $profileKey) {
                throw "Профіль користувача $InteractiveUserSid не завантажено. Увійдіть у цей обліковий запис і повторіть встановлення."
            }
            $profileKey.Dispose()
            $profileKey = $null
        }

        $runKey = $baseKey.OpenSubKey($subKeyPath, $true)
        if (-not $runKey) {
            $runKey = $baseKey.CreateSubKey($subKeyPath, $true)
        }
        if (-not $runKey) {
            throw 'Не вдалося відкрити розділ автозапуску інтерактивного користувача.'
        }

        $valueExists = $runKey.GetValueNames() -contains $script:EdgeAgentStartupValueName
        $previousValue = if ($valueExists) {
            $runKey.GetValue($script:EdgeAgentStartupValueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        } else {
            $null
        }
        $previousValueKind = if ($valueExists) {
            $runKey.GetValueKind($script:EdgeAgentStartupValueName)
        } else {
            $null
        }
        $startupCommand = '"{0}"' -f $canonicalExecutablePath

        try {
            $runKey.SetValue(
                $script:EdgeAgentStartupValueName,
                $startupCommand,
                [Microsoft.Win32.RegistryValueKind]::String
            )
        } catch {
            if ($valueExists) {
                $runKey.SetValue(
                    $script:EdgeAgentStartupValueName,
                    $previousValue,
                    $previousValueKind
                )
            } else {
                $runKey.DeleteValue($script:EdgeAgentStartupValueName, $false)
            }

            throw
        }

        return $startupCommand
    } finally {
        if ($profileKey) { $profileKey.Dispose() }
        if ($runKey) { $runKey.Dispose() }
        $baseKey.Dispose()
    }
}

function Remove-EdgeAgentDesktopStartupEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InteractiveUserSid,
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::Users,
        [string]$RunSubKeyPath
    )

    $subKeyPath = Get-EdgeAgentStartupRegistrySubKeyPath `
        -InteractiveUserSid $InteractiveUserSid `
        -RunSubKeyPath $RunSubKeyPath
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Default)
    $profileKey = $null
    $runKey = $null

    try {
        if ($RegistryHive -eq [Microsoft.Win32.RegistryHive]::Users) {
            $profileKey = $baseKey.OpenSubKey($InteractiveUserSid, $false)
            if (-not $profileKey) {
                throw "Профіль користувача $InteractiveUserSid не завантажено. Увійдіть у цей обліковий запис і повторіть видалення."
            }
            $profileKey.Dispose()
            $profileKey = $null
        }

        $runKey = $baseKey.OpenSubKey($subKeyPath, $true)
        if (-not $runKey) { return }

        $runKey.DeleteValue($script:EdgeAgentStartupValueName, $false)
    } finally {
        if ($profileKey) { $profileKey.Dispose() }
        if ($runKey) { $runKey.Dispose() }
        $baseKey.Dispose()
    }
}

Export-ModuleMember -Function @(
    'Assert-EdgeAgentInteractiveUserSid',
    'Get-EdgeAgentInteractiveUserSidForContext',
    'Resolve-EdgeAgentInteractiveUserSid',
    'Get-EdgeAgentStartupRegistrySubKeyPath',
    'Test-EdgeAgentInteractiveUserProfileLoaded',
    'Get-EdgeAgentServiceStartupType',
    'Set-EdgeAgentDesktopStartupEntry',
    'Remove-EdgeAgentDesktopStartupEntry'
)
