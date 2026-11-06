Set-StrictMode -Version Latest

$script:EdgeAgentStartupValueName = 'KGTA.Alert.OperatorPanel'
$script:EdgeAgentRunSubKeySuffix = 'Software\Microsoft\Windows\CurrentVersion\Run'

function Resolve-EdgeAgentInteractiveUserSid {
    [CmdletBinding()]
    param([string]$InteractiveUser)

    if ([string]::IsNullOrWhiteSpace($InteractiveUser)) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if ($identity.IsSystem) {
            throw 'Під час встановлення від імені SYSTEM потрібно явно вказати -InteractiveUser для інтерактивного облікового запису.'
        }

        return $identity.User.Value
    }

    if ($InteractiveUser -match '^S-\d-(?:\d+-)*\d+$') {
        return ([Security.Principal.SecurityIdentifier]::new($InteractiveUser)).Value
    }

    try {
        return ([Security.Principal.NTAccount]::new($InteractiveUser)).Translate(
            [Security.Principal.SecurityIdentifier]
        ).Value
    } catch [System.Security.Principal.IdentityNotMappedException] {
        throw "Не вдалося визначити SID інтерактивного користувача '$InteractiveUser'."
    }
}

function Get-EdgeAgentStartupRegistrySubKeyPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InteractiveUserSid,
        [string]$RunSubKeyPath
    )

    if ($InteractiveUserSid -notmatch '^S-\d-(?:\d+-)*\d+$') {
        throw 'SID інтерактивного користувача має некоректний формат.'
    }

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

    if ($InteractiveUserSid -notmatch '^S-\d-(?:\d+-)*\d+$') {
        throw 'SID інтерактивного користувача має некоректний формат.'
    }

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
    'Resolve-EdgeAgentInteractiveUserSid',
    'Get-EdgeAgentStartupRegistrySubKeyPath',
    'Test-EdgeAgentInteractiveUserProfileLoaded',
    'Get-EdgeAgentServiceStartupType',
    'Set-EdgeAgentDesktopStartupEntry',
    'Remove-EdgeAgentDesktopStartupEntry'
)
