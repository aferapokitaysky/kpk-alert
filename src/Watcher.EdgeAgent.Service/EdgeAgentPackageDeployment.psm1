Set-StrictMode -Version Latest

function New-EdgeAgentPackageStage {
    param(
        [Parameter(Mandatory = $true)][string]$ServicePublishDirectory,
        [Parameter(Mandatory = $true)][string]$DesktopPublishDirectory,
        [Parameter(Mandatory = $true)][string]$InstallDirectory
    )

    foreach ($directory in @($ServicePublishDirectory, $DesktopPublishDirectory)) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            throw "Каталог пакета не знайдено: $directory"
        }
    }

    $target = [IO.Path]::GetFullPath($InstallDirectory)
    $parent = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $leaf = Split-Path -Leaf $target
    $stage = Join-Path $parent ('.{0}.stage-{1}' -f $leaf, [guid]::NewGuid().ToString('N'))
    $backup = Join-Path $parent ('.{0}.backup-{1}' -f $leaf, [guid]::NewGuid().ToString('N'))

    try {
        New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
        Copy-Item -Path (Join-Path $ServicePublishDirectory '*') -Destination $stage -Recurse -Force -ErrorAction Stop
        Copy-Item -Path (Join-Path $DesktopPublishDirectory '*') -Destination $stage -Recurse -Force -ErrorAction Stop
        return [pscustomobject]@{
            InstallDirectory = $target
            StageDirectory = $stage
            BackupDirectory = $backup
            HadPreviousPackage = Test-Path -LiteralPath $target -PathType Container
        }
    } catch {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        throw
    }
}

function Install-EdgeAgentPackageStage {
    param([Parameter(Mandatory = $true)][psobject]$Deployment)

    if (Test-Path -LiteralPath $Deployment.InstallDirectory) {
        Move-Item -LiteralPath $Deployment.InstallDirectory -Destination $Deployment.BackupDirectory -ErrorAction Stop
    }
    try {
        Move-Item -LiteralPath $Deployment.StageDirectory -Destination $Deployment.InstallDirectory -ErrorAction Stop
    } catch {
        if ($Deployment.HadPreviousPackage -and (Test-Path -LiteralPath $Deployment.BackupDirectory)) {
            Move-Item -LiteralPath $Deployment.BackupDirectory -Destination $Deployment.InstallDirectory -ErrorAction Stop
        }
        throw
    }
}

function Restore-EdgeAgentPackage {
    param([Parameter(Mandatory = $true)][psobject]$Deployment)

    if ($Deployment.HadPreviousPackage -and -not (Test-Path -LiteralPath $Deployment.BackupDirectory -PathType Container)) {
        throw 'Резервний каталог попереднього пакета відсутній; автоматичне відновлення неможливе.'
    }
    if (Test-Path -LiteralPath $Deployment.InstallDirectory) {
        Remove-Item -LiteralPath $Deployment.InstallDirectory -Recurse -Force -ErrorAction Stop
    }
    if ($Deployment.HadPreviousPackage) {
        Move-Item -LiteralPath $Deployment.BackupDirectory -Destination $Deployment.InstallDirectory -ErrorAction Stop
    }
}

function Complete-EdgeAgentPackageDeployment {
    param([Parameter(Mandatory = $true)][psobject]$Deployment)

    foreach ($path in @($Deployment.StageDirectory, $Deployment.BackupDirectory)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop }
    }
}

Export-ModuleMember -Function New-EdgeAgentPackageStage, Install-EdgeAgentPackageStage, Restore-EdgeAgentPackage, Complete-EdgeAgentPackageDeployment
