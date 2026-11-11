using Microsoft.Win32;

namespace Watcher.EdgeAgent.Core;

public sealed class InstallerMaintenanceGate(
    Func<bool>? maintenanceMarkerExists = null,
    Func<bool>? recoveryMarkerExists = null)
{
    public const string RegistryPath = @"SOFTWARE\KGTA\Alert\EdgeAgent\InstallerState";
    public const string MaintenanceMarkerValueName = "MaintenanceActive";
    public const string RecoveryMarkerValueName = "RecoveryRequired";

    public bool IsHeldByInstaller()
    {
        return IsMarkerSet(maintenanceMarkerExists, MaintenanceMarkerValueName) ||
            IsMarkerSet(recoveryMarkerExists, RecoveryMarkerValueName);
    }

    private static bool IsMarkerSet(Func<bool>? markerExists, string valueName)
    {
        try
        {
            return markerExists?.Invoke() ?? IsMarkerSetInRegistry(valueName);
        }
        catch
        {
            // Якщо захищений стан недоступний, START не можна вважати безпечним.
            return true;
        }
    }

    private static bool IsMarkerSetInRegistry(string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return IsMarkerSetInRegistry(RegistryHive.LocalMachine, RegistryPath, valueName);
    }

    internal static bool IsMaintenanceMarkerSetInRegistry(RegistryHive registryHive, string registryPath)
    {
        return IsMarkerSetInRegistry(registryHive, registryPath, MaintenanceMarkerValueName);
    }

    internal static bool IsRecoveryMarkerSetInRegistry(RegistryHive registryHive, string registryPath)
    {
        return IsMarkerSetInRegistry(registryHive, registryPath, RecoveryMarkerValueName);
    }

    private static bool IsMarkerSetInRegistry(RegistryHive registryHive, string registryPath, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Маркери стану інсталятора доступні лише у Windows Registry.");
        }

        using var baseKey = RegistryKey.OpenBaseKey(registryHive, RegistryView.Registry64);
        using RegistryKey? installerState = baseKey.OpenSubKey(registryPath, writable: false);
        if (installerState is null)
        {
            return false;
        }

        object? marker = installerState.GetValue(
            valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (marker is null)
        {
            return false;
        }

        return marker is not string markerValue || markerValue switch
        {
            "0" => false,
            "1" => true,
            _ => true
        };
    }
}
