Set-StrictMode -Version Latest

if (-not ('Kgta.Alert.Security.NativeAcl' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kgta.Alert.Security
{
    public static class NativeAcl
    {
        private const uint OwnerSecurityInformation = 0x1;
        private const uint GroupSecurityInformation = 0x2;
        private const uint DaclSecurityInformation = 0x4;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;
        private const uint UnprotectedDaclSecurityInformation = 0x20000000;
        private const ushort DaclProtectedControl = 0x1000;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetNamedSecurityInfo(string name, int objectType, uint securityInformation,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr securityDescriptor,
            uint revision, uint securityInformation, out IntPtr stringSecurityDescriptor, out uint stringLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string stringSecurityDescriptor,
            uint revision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorControl(IntPtr securityDescriptor, out ushort control, out uint revision);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorDacl(IntPtr securityDescriptor, out bool daclPresent,
            out IntPtr dacl, out bool daclDefaulted);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SetNamedSecurityInfo(string name, int objectType, uint securityInformation,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInformation,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static string CaptureDaclSddl(string path, out bool protectedDacl)
        {
            IntPtr descriptor = IntPtr.Zero;
            IntPtr stringDescriptor = IntPtr.Zero;
            try
            {
                IntPtr owner, group, dacl, sacl;
                uint result = GetNamedSecurityInfo(path, 1, OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
                    out owner, out group, out dacl, out sacl, out descriptor);
                if (result != 0) throw new Win32Exception((int)result, "Не вдалося прочитати ACL для " + path);

                ushort control;
                uint revision;
                if (!GetSecurityDescriptorControl(descriptor, out control, out revision))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося прочитати прапорці ACL для " + path);
                protectedDacl = (control & DaclProtectedControl) != 0;

                uint length;
                if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1,
                    OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
                    out stringDescriptor, out length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося зберегти ACL для " + path);
                return Marshal.PtrToStringUni(stringDescriptor);
            }
            finally
            {
                if (stringDescriptor != IntPtr.Zero) LocalFree(stringDescriptor);
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            }
        }

        public static IntPtr OpenAclHandle(string path)
        {
            const uint ReadControl = 0x00020000;
            const uint ShareAll = 0x1 | 0x2 | 0x4;
            const uint OpenExisting = 3;
            const uint BackupSemantics = 0x02000000;
            IntPtr handle = CreateFile(path, ReadControl, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося відкрити дескриптор ACL для " + path);
            return handle;
        }

        public static void CloseAclHandle(IntPtr handle)
        {
            if (!CloseHandle(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося закрити дескриптор ACL.");
        }

        public static string CaptureDaclSddlFromHandle(IntPtr handle, out bool protectedDacl)
        {
            IntPtr descriptor = IntPtr.Zero;
            IntPtr stringDescriptor = IntPtr.Zero;
            try
            {
                IntPtr owner, group, dacl, sacl;
                uint result = GetSecurityInfo(handle, 1, OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
                    out owner, out group, out dacl, out sacl, out descriptor);
                if (result != 0) throw new Win32Exception((int)result, "Не вдалося прочитати ACL через відкритий дескриптор.");

                ushort control;
                uint revision;
                if (!GetSecurityDescriptorControl(descriptor, out control, out revision))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося прочитати прапорці ACL через відкритий дескриптор.");
                protectedDacl = (control & DaclProtectedControl) != 0;

                uint length;
                if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1,
                    OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
                    out stringDescriptor, out length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося перетворити ACL на SDDL.");
                return Marshal.PtrToStringUni(stringDescriptor);
            }
            finally
            {
                if (stringDescriptor != IntPtr.Zero) LocalFree(stringDescriptor);
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            }
        }

        public static void RestoreDacl(string path, string sddl, bool protectedDacl)
        {
            IntPtr descriptor = IntPtr.Zero;
            try
            {
                uint size;
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out descriptor, out size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Не вдалося підготувати ACL для відновлення " + path);

                bool present, defaulted;
                IntPtr dacl;
                if (!GetSecurityDescriptorDacl(descriptor, out present, out dacl, out defaulted) || !present)
                    throw new InvalidOperationException("Збережений дескриптор не містить DACL для " + path);

                uint information = DaclSecurityInformation |
                    (protectedDacl ? ProtectedDaclSecurityInformation : UnprotectedDaclSecurityInformation);
                uint result = SetNamedSecurityInfo(path, 1, information, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
                if (result != 0) throw new Win32Exception((int)result, "Не вдалося відновити ACL для " + path);
            }
            finally
            {
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            }
        }
    }
}
'@
}

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

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($path in @($Deployment.StageDirectory, $Deployment.BackupDirectory)) {
        try {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            }
        } catch {
            $failures.Add("$path`: $_")
        }
    }

    return [pscustomobject]@{
        Succeeded = $failures.Count -eq 0
        Failures = $failures.ToArray()
    }
}

function Invoke-EdgeAgentInstallTransaction {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][scriptblock]$Rollback
    )

    try {
        & $Action
    } catch {
        $failure = $_
        try {
            & $Rollback
        } catch {
            throw "Операція інсталяції не пройшла, а rollback завершився помилкою: $_. Початкова помилка: $failure"
        }
        throw $failure
    }
}

function Backup-EdgeAgentOperatorShortcut {
    param([Parameter(Mandatory = $true)][string]$ShortcutPath)

    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) { return $null }
    $backupPath = '{0}.rollback-{1}' -f $ShortcutPath, [guid]::NewGuid().ToString('N')
    Copy-Item -LiteralPath $ShortcutPath -Destination $backupPath -ErrorAction Stop
    return $backupPath
}

function Restore-EdgeAgentOperatorShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [string]$BackupPath,
        [bool]$Created
    )

    if ($BackupPath -and (Test-Path -LiteralPath $BackupPath -PathType Leaf)) {
        Move-Item -LiteralPath $BackupPath -Destination $ShortcutPath -Force -ErrorAction Stop
    } elseif ($Created -and (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
        Remove-Item -LiteralPath $ShortcutPath -Force -ErrorAction Stop
    }
}

function Add-EdgeAgentOperatorGroupMembers {
    param(
        [Parameter(Mandatory = $true)][string[]]$Members,
        [Parameter(Mandatory = $true)][scriptblock]$GetMembers,
        [Parameter(Mandatory = $true)][scriptblock]$AddMember,
        [Parameter(Mandatory = $true)][scriptblock]$RemoveMember
    )

    $addedMembers = [System.Collections.Generic.List[string]]::new()
    try {
        foreach ($member in $Members) {
            $existingMembers = @(& $GetMembers)
            if ($existingMembers -contains $member) { continue }
            $null = & $AddMember $member
            $addedMembers.Add($member)
        }
    } catch {
        $addFailure = $_
        if ($addedMembers.Count -gt 0) {
            try {
                Remove-EdgeAgentOperatorGroupMembers -Members $addedMembers.ToArray() -RemoveMember $RemoveMember
            } catch {
                throw "Не вдалося додати членство операторів: $addFailure. Часткове членство також не вдалося скасувати: $_"
            }
        }
        throw $addFailure
    }
    return ,$addedMembers.ToArray()
}

function Remove-EdgeAgentOperatorGroupMembers {
    param(
        [Parameter(Mandatory = $true)][string[]]$Members,
        [Parameter(Mandatory = $true)][scriptblock]$RemoveMember
    )

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($member in $Members) {
        try { & $RemoveMember $member }
        catch { $failures.Add("$member`: $_") }
    }
    if ($failures.Count -gt 0) {
        throw "Не вдалося скасувати додане членство в групі операторів: $($failures -join '; ')"
    }
}

function Get-EdgeAgentAclSnapshot {
    param(
        [Parameter(Mandatory = $true)][string[]]$Path,
        [string[]]$RequiredPath = @()
    )

    $snapshots = [System.Collections.Generic.List[object]]::new()
    foreach ($requiredItemPath in $RequiredPath) {
        if (-not (Test-Path -LiteralPath $requiredItemPath)) {
            throw "Обов’язковий шлях для збереження ACL не існує: $requiredItemPath"
        }
    }
    foreach ($itemPath in $Path) {
        if (Test-Path -LiteralPath $itemPath) {
            $protectedDacl = $false
            $sddl = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddl($itemPath, [ref]$protectedDacl)
            $snapshots.Add([pscustomobject]@{
                Path = $itemPath
                Sddl = $sddl
                ProtectedDacl = $protectedDacl
            })
        }
    }
    return ,$snapshots.ToArray()
}

function Restore-EdgeAgentAclSnapshot {
    param([Parameter(Mandatory = $true)][object[]]$Snapshot)

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $Snapshot) {
        try {
            if (-not (Test-Path -LiteralPath $item.Path)) {
                throw 'Шлях, для якого збережено ACL, більше не існує.'
            }
            [Kgta.Alert.Security.NativeAcl]::RestoreDacl($item.Path, $item.Sddl, [bool]$item.ProtectedDacl)
            $restoredProtectedDacl = $false
            $restoredSddl = [Kgta.Alert.Security.NativeAcl]::CaptureDaclSddl($item.Path, [ref]$restoredProtectedDacl)
            if ($restoredSddl -cne $item.Sddl -or $restoredProtectedDacl -ne $item.ProtectedDacl) {
                throw 'Після відновлення дескриптор відрізняється від збереженого.'
            }
        } catch {
            $failures.Add("$($item.Path): $_")
        }
    }
    if ($failures.Count -gt 0) {
        throw "Не вдалося відновити початкові ACL: $($failures -join '; ')"
    }
}

function Remove-EdgeAgentConfigurationAfterFailedInstall {
    param(
        [Parameter(Mandatory = $true)][string]$ConfigurationPath,
        [Parameter(Mandatory = $true)][bool]$ExistedBeforeInstall
    )

    if ($ExistedBeforeInstall -or -not (Test-Path -LiteralPath $ConfigurationPath)) { return }
    if (-not (Test-Path -LiteralPath $ConfigurationPath -PathType Leaf)) {
        throw "Шлях конфігурації, створеної під час інсталяції, більше не є файлом: $ConfigurationPath"
    }
    Remove-Item -LiteralPath $ConfigurationPath -Force -ErrorAction Stop
}

function Restore-EdgeAgentServiceStartupAfterRollback {
    param(
        [Parameter(Mandatory = $true)][string]$PreviousStartupType,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$RollbackFailures,
        [Parameter(Mandatory = $true)][scriptblock]$SetStartupType
    )

    if ($RollbackFailures.Count -eq 0) {
        try {
            if (& $SetStartupType $PreviousStartupType) { return }
            $RollbackFailures.Add("Не вдалося відновити тип запуску '$PreviousStartupType'.")
        } catch {
            $RollbackFailures.Add("Не вдалося відновити тип запуску '$PreviousStartupType': $_")
        }
    }

    try {
        if (-not (& $SetStartupType 'disabled')) {
            $RollbackFailures.Add('Не вдалося вимкнути автозапуск служби після неповного rollback.')
        }
    } catch {
        $RollbackFailures.Add("Не вдалося вимкнути автозапуск служби після неповного rollback: $_")
    }
}

Export-ModuleMember -Function New-EdgeAgentPackageStage, Install-EdgeAgentPackageStage, Restore-EdgeAgentPackage, Complete-EdgeAgentPackageDeployment, Invoke-EdgeAgentInstallTransaction, Backup-EdgeAgentOperatorShortcut, Restore-EdgeAgentOperatorShortcut, Add-EdgeAgentOperatorGroupMembers, Remove-EdgeAgentOperatorGroupMembers, Get-EdgeAgentAclSnapshot, Restore-EdgeAgentAclSnapshot, Remove-EdgeAgentConfigurationAfterFailedInstall, Restore-EdgeAgentServiceStartupAfterRollback
