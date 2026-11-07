[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'EdgeAgentStartupRegistration.psm1') -Force

$interactiveUserSid = Resolve-EdgeAgentInteractiveUserSid
if ($Remove) {
    Remove-EdgeAgentDesktopStartupEntry -InteractiveUserSid $interactiveUserSid
    Write-Output 'Автозапуск панелі вимкнено для поточного користувача.'
    return
}

$service = Get-Service -Name 'KGTA.Alert.EdgeAgent' -ErrorAction SilentlyContinue
if (-not $service) {
    throw 'Windows-вузол не встановлено. Зверніться до адміністратора.'
}

$executablePath = Get-EdgeAgentInstalledDesktopExecutablePath
Set-EdgeAgentDesktopStartupEntry `
    -InteractiveUserSid $interactiveUserSid `
    -ExecutablePath $executablePath | Out-Null

Write-Output 'Автозапуск панелі ввімкнено для поточного користувача.'
