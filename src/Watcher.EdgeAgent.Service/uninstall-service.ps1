[CmdletBinding()]
param(
    [switch]$RemoveLocalData,
    [string]$InteractiveUser
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'EdgeAgentStartupRegistration.psm1') -Force
$serviceName = 'KGTA.Alert.EdgeAgent'
$dataDirectory = Join-Path $env:ProgramData 'KGTA\Alert'
$installDirectory = Join-Path $env:ProgramFiles 'KGTA\Alert\EdgeAgent'
$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\KGTA Alert'
$regExecutable = Join-Path $env:SystemRoot 'System32\reg.exe'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустіть видалення від імені адміністратора.'
}
$interactiveUserSid = Resolve-EdgeAgentInteractiveUserSid -InteractiveUser $InteractiveUser
if (-not (Test-Path -LiteralPath $regExecutable -PathType Leaf)) {
    throw 'Не знайдено системну утиліту керування реєстром. Службу й файли не змінено.'
}

$profileCleanupPlan = New-EdgeAgentStartupCleanupPlan `
    -Profiles @(Get-CimInstance -ClassName Win32_UserProfile -Filter 'Special = FALSE') `
    -RequiredInteractiveUserSid $interactiveUserSid `
    -RegExecutablePath $regExecutable
try {
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        $service = Get-Service -Name $serviceName
        if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        }
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Не вдалося видалити службу.' }
    }

    Remove-EdgeAgentDesktopStartupEntriesForProfiles -Profiles $profileCleanupPlan.Profiles
} finally {
    if ($profileCleanupPlan.MountedHiveNames.Count -gt 0) {
        Release-EdgeAgentProfileHives `
            -MountedHiveNames $profileCleanupPlan.MountedHiveNames `
            -RegExecutablePath $regExecutable
    }
}

foreach ($directory in @($installDirectory, $startMenu)) {
    if (Test-Path -LiteralPath $directory) {
        $resolvedDirectory = (Resolve-Path -LiteralPath $directory).Path
        $expectedDirectory = [IO.Path]::GetFullPath($directory)
        if ($resolvedDirectory -ne $expectedDirectory) { throw "Шлях видалення не збігається з очікуваним: $directory" }
        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}
if ($RemoveLocalData) {
    if (Test-Path -LiteralPath $dataDirectory) {
        $resolvedData = (Resolve-Path -LiteralPath $dataDirectory).Path
        $expectedData = [IO.Path]::GetFullPath($dataDirectory)
        if ($resolvedData -ne $expectedData) { throw 'Шлях даних не збігається з очікуваним.' }
        Remove-Item -LiteralPath $resolvedData -Recurse -Force
    }
}
Write-Output 'Службу видалено. Локальні дані збережено, якщо не задано -RemoveLocalData.'
