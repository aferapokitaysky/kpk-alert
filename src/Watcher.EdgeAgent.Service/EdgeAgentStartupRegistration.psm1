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

function Get-EdgeAgentInstalledDesktopExecutablePath {
    [CmdletBinding()]
    param([string]$ProgramFilesDirectory = $env:ProgramFiles)

    if ([string]::IsNullOrWhiteSpace($ProgramFilesDirectory) -or
        -not [IO.Path]::IsPathRooted($ProgramFilesDirectory)) {
        throw 'Не вдалося визначити абсолютний каталог Program Files.'
    }

    $installDirectory = Join-Path ([IO.Path]::GetFullPath($ProgramFilesDirectory)) 'KGTA\Alert\EdgeAgent'
    $executablePath = Join-Path $installDirectory 'Watcher.EdgeAgent.Desktop.exe'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw 'Не знайдено встановлену панель KGTA Alert. Спочатку адміністратор має встановити Windows-вузол.'
    }

    return [IO.Path]::GetFullPath($executablePath)
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

function Remove-EdgeAgentDesktopStartupEntriesForProfiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Profiles,
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::Users
    )

    if ($RegistryHive -ne [Microsoft.Win32.RegistryHive]::Users -and
        $RegistryHive -ne [Microsoft.Win32.RegistryHive]::CurrentUser) {
        throw 'Очищення автозапуску дозволено лише в профілях користувачів.'
    }
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Default)
    $removedEntries = @()
    try {
        foreach ($profile in $Profiles) {
            if (-not $profile.Sid -or -not $profile.RunSubKeyPath) {
                throw 'План очищення автозапуску містить неповний запис профілю.'
            }

            $subKeyPath = Get-EdgeAgentStartupRegistrySubKeyPath `
                -InteractiveUserSid ([string]$profile.Sid) `
                -RunSubKeyPath ([string]$profile.RunSubKeyPath)
            $runKey = $baseKey.OpenSubKey($subKeyPath, $true)
            if (-not $runKey) { continue }

            try {
                if (-not ($runKey.GetValueNames() -contains $script:EdgeAgentStartupValueName)) { continue }

                $removedEntries += [pscustomobject]@{
                    Sid = [string]$profile.Sid
                    RunSubKeyPath = $subKeyPath
                    Value = $runKey.GetValue(
                        $script:EdgeAgentStartupValueName,
                        $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames
                    )
                    Kind = $runKey.GetValueKind($script:EdgeAgentStartupValueName)
                }
                $runKey.DeleteValue($script:EdgeAgentStartupValueName, $false)
            } finally {
                $runKey.Dispose()
            }
        }
        return @($removedEntries)
    } catch {
        $failure = $_
        if ($removedEntries.Count -gt 0) {
            try {
                Restore-EdgeAgentDesktopStartupEntriesForProfiles `
                    -Entries $removedEntries `
                    -RegistryHive $RegistryHive
            } catch {
                throw "Не вдалося повністю очистити записи Run або відновити їх після помилки. Причина: $failure. Помилка відновлення: $($_.Exception.Message)"
            }
        }
        throw $failure
    } finally {
        $baseKey.Dispose()
    }
}

function Release-EdgeAgentProfileHives {
    [CmdletBinding()]
    param(
        [string[]]$MountedHiveNames,
        [Parameter(Mandatory = $true)][string]$RegExecutablePath
    )

    $failedMounts = @()
    for ($index = $MountedHiveNames.Count - 1; $index -ge 0; $index--) {
        $mountName = $MountedHiveNames[$index]
        $output = & $RegExecutablePath unload "HKU\$mountName" 2>&1
        if ($LASTEXITCODE -ne 0) { $failedMounts += $mountName }
    }

    if ($failedMounts.Count -gt 0) {
        throw "Не вдалося від'єднати тимчасові профілі реєстру: $($failedMounts -join ', '). Закрийте їх через reg.exe unload і повторіть видалення."
    }
}

function New-EdgeAgentStartupCleanupPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Profiles,
        [Parameter(Mandatory = $true)][string]$RequiredInteractiveUserSid,
        [Parameter(Mandatory = $true)][string]$RegExecutablePath,
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::Users,
        [hashtable]$RegistryRoots = @{}
    )

    if ($RegistryHive -ne [Microsoft.Win32.RegistryHive]::Users -and
        $RegistryHive -ne [Microsoft.Win32.RegistryHive]::CurrentUser) {
        throw 'План очищення автозапуску дозволено створювати лише для профілів користувачів.'
    }
    $interactiveSid = Assert-EdgeAgentInteractiveUserSid -InteractiveUserSid $RequiredInteractiveUserSid
    if (-not ($Profiles | Where-Object { $_.SID -eq $interactiveSid -and -not $_.Special })) {
        throw "Не знайдено профіль інтерактивного користувача $interactiveSid. Службу й файли не змінено."
    }

    $cleanupProfiles = @()
    $mountedHiveNames = @()
    $usersHive = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Default)
    try {
        foreach ($profile in $Profiles) {
            if ($profile.Special) { continue }
            $sid = [string]$profile.SID
            if ([string]::IsNullOrWhiteSpace($sid) -or
                $sid -match '^S-1-5-(?:18|19|20)$|^S-1-5-(?:80|82|83)-') {
                continue
            }
            $sid = Assert-EdgeAgentInteractiveUserSid -InteractiveUserSid $sid

            if ($RegistryRoots.ContainsKey($sid)) {
                $registryRoot = [string]$RegistryRoots[$sid]
            } elseif ($RegistryHive -eq [Microsoft.Win32.RegistryHive]::CurrentUser) {
                throw "Для тестового профілю $sid не передано корінь реєстру."
            } else {
                $rootKey = $usersHive.OpenSubKey($sid, $false)
                if ($rootKey) {
                    $rootKey.Dispose()
                    $registryRoot = $sid
                } else {
                    if ($profile.Loaded) {
                        throw "Профіль $sid позначено завантаженим, але його hive відсутній у HKEY_USERS. Службу й файли не змінено."
                    }

                    $profilePath = [string]$profile.LocalPath
                    if ([string]::IsNullOrWhiteSpace($profilePath)) {
                        throw "Для профілю $sid не визначено каталог. Службу й файли не змінено."
                    }
                    $profileHivePath = Join-Path $profilePath 'NTUSER.DAT'
                    if (-not (Test-Path -LiteralPath $profileHivePath -PathType Leaf)) {
                        continue
                    }
                    if (-not (Test-Path -LiteralPath $RegExecutablePath -PathType Leaf)) {
                        throw 'Не знайдено системну утиліту керування реєстром. Службу й файли не змінено.'
                    }

                    $mountName = 'KGTA.Alert.Uninstall.' + [guid]::NewGuid().ToString('N')
                    $output = & $RegExecutablePath load "HKU\$mountName" $profileHivePath 2>&1
                    if ($LASTEXITCODE -ne 0) {
                        throw "Не вдалося підготувати реєстр профілю $sid. Закрийте сеанс цього користувача або від'єднайте зайнятий hive й повторіть видалення; служба й файли не змінені."
                    }
                    $mountedHiveNames += $mountName
                    $registryRoot = $mountName
                }
            }

            $runSubKeyPath = "$registryRoot\Software\Microsoft\Windows\CurrentVersion\Run"
            $validatedRunPath = Get-EdgeAgentStartupRegistrySubKeyPath `
                -InteractiveUserSid $sid `
                -RunSubKeyPath $runSubKeyPath
            $runKey = $usersHive.OpenSubKey($validatedRunPath, $true)
            if ($runKey) { $runKey.Dispose() }
            $cleanupProfiles += [pscustomobject]@{
                Sid = $sid
                RunSubKeyPath = $validatedRunPath
            }
        }

        return [pscustomobject]@{
            Profiles = @($cleanupProfiles)
            MountedHiveNames = @($mountedHiveNames)
        }
    } catch {
        $failure = $_
        $usersHive.Dispose()
        $usersHive = $null
        if ($mountedHiveNames.Count -gt 0) {
            try { Release-EdgeAgentProfileHives -MountedHiveNames $mountedHiveNames -RegExecutablePath $RegExecutablePath }
            catch { throw "$failure`n$($_.Exception.Message)" }
        }
        throw $failure
    } finally {
        if ($usersHive) { $usersHive.Dispose() }
    }
}

function Restore-EdgeAgentDesktopStartupEntriesForProfiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Entries,
        [Microsoft.Win32.RegistryHive]$RegistryHive = [Microsoft.Win32.RegistryHive]::Users
    )

    if ($RegistryHive -ne [Microsoft.Win32.RegistryHive]::Users -and
        $RegistryHive -ne [Microsoft.Win32.RegistryHive]::CurrentUser) {
        throw 'Відновлення автозапуску дозволено лише в профілях користувачів.'
    }

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($RegistryHive, [Microsoft.Win32.RegistryView]::Default)
    try {
        foreach ($entry in $Entries) {
            if (-not $entry.Sid -or -not $entry.RunSubKeyPath) {
                throw 'Список відновлення автозапуску містить неповний запис профілю.'
            }

            $subKeyPath = Get-EdgeAgentStartupRegistrySubKeyPath `
                -InteractiveUserSid ([string]$entry.Sid) `
                -RunSubKeyPath ([string]$entry.RunSubKeyPath)
            $runKey = $baseKey.OpenSubKey($subKeyPath, $true)
            if (-not $runKey) { $runKey = $baseKey.CreateSubKey($subKeyPath, $true) }
            if (-not $runKey) { throw "Не вдалося відкрити Run-розділ профілю $($entry.Sid) для відновлення." }

            try {
                if ($runKey.GetValueNames() -contains $script:EdgeAgentStartupValueName) {
                    $currentValue = $runKey.GetValue(
                        $script:EdgeAgentStartupValueName,
                        $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames
                    )
                    if ($currentValue -eq $entry.Value -and
                        $runKey.GetValueKind($script:EdgeAgentStartupValueName) -eq $entry.Kind) {
                        continue
                    }
                    throw "Значення автозапуску профілю $($entry.Sid) змінилося під час видалення; його не перезаписано."
                }

                $runKey.SetValue($script:EdgeAgentStartupValueName, $entry.Value, $entry.Kind)
            } finally {
                $runKey.Dispose()
            }
        }
    } finally {
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
    'Get-EdgeAgentInstalledDesktopExecutablePath',
    'Set-EdgeAgentDesktopStartupEntry',
    'Remove-EdgeAgentDesktopStartupEntry',
    'Remove-EdgeAgentDesktopStartupEntriesForProfiles',
    'New-EdgeAgentStartupCleanupPlan',
    'Release-EdgeAgentProfileHives',
    'Restore-EdgeAgentDesktopStartupEntriesForProfiles'
)
