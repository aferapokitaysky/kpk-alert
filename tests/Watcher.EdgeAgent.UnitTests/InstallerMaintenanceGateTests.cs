using System.Runtime.Versioning;
using Microsoft.Win32;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.UnitTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerMaintenanceGateTests
{
    [Fact]
    public void IsHeldByInstaller_ReturnsTrueWhenMaintenanceMarkerExists()
    {
        InstallerMaintenanceGate gate = new(() => true, () => false);

        Assert.True(gate.IsHeldByInstaller());
    }

    [Fact]
    public void IsHeldByInstaller_ReturnsTrueWhenRecoveryMarkerExists()
    {
        InstallerMaintenanceGate gate = new(() => false, () => true);

        Assert.True(gate.IsHeldByInstaller());
    }

    [Fact]
    public void IsHeldByInstaller_ReturnsFalseWhenMarkersAreAbsent()
    {
        InstallerMaintenanceGate gate = new(() => false, () => false);

        Assert.False(gate.IsHeldByInstaller());
    }

    [Fact]
    public void IsHeldByInstaller_FailsClosedWhenEitherMarkerCannotBeRead()
    {
        InstallerMaintenanceGate maintenanceReadFailure = new(
            () => throw new UnauthorizedAccessException(), () => false);
        InstallerMaintenanceGate recoveryReadFailure = new(
            () => false, () => throw new UnauthorizedAccessException());

        Assert.True(maintenanceReadFailure.IsHeldByInstaller());
        Assert.True(recoveryReadFailure.IsHeldByInstaller());
    }

    [Fact]
    public void IsHeldByInstaller_IgnoresPrecreatedShadowMutexWhenMarkersAreClear()
    {
        var mutexName = $@"Local\KGTA.Alert.EdgeAgent.Tests.{Guid.NewGuid():N}";
        using Mutex shadowMutex = new(initiallyOwned: true, mutexName, out var createdNew);
        Assert.True(createdNew);

        InstallerMaintenanceGate gate = new(() => false, () => false);

        Assert.False(gate.IsHeldByInstaller());
        shadowMutex.ReleaseMutex();
    }

    [Fact]
    public void RegistryReader_ReturnsFalseWhenKeyOrMarkerIsAbsent()
    {
        WithTemporaryRegistryPath((registryHive, registryPath) =>
        {
            Assert.False(InstallerMaintenanceGate.IsMaintenanceMarkerSetInRegistry(registryHive, registryPath));
            Assert.False(InstallerMaintenanceGate.IsRecoveryMarkerSetInRegistry(registryHive, registryPath));

            using var baseKey = RegistryKey.OpenBaseKey(registryHive, RegistryView.Registry64);
            using RegistryKey markerKey = baseKey.CreateSubKey(registryPath)!;
            Assert.False(InstallerMaintenanceGate.IsMaintenanceMarkerSetInRegistry(registryHive, registryPath));
            Assert.False(InstallerMaintenanceGate.IsRecoveryMarkerSetInRegistry(registryHive, registryPath));
        });
    }

    [Theory]
    [InlineData(InstallerMaintenanceGate.MaintenanceMarkerValueName)]
    [InlineData(InstallerMaintenanceGate.RecoveryMarkerValueName)]
    public void RegistryReader_BlocksWhileMarkerRemainsSetAfterRestart(string valueName)
    {
        WithTemporaryRegistryPath((registryHive, registryPath) =>
        {
            SetMarker(registryHive, registryPath, valueName, "1", RegistryValueKind.String);

            bool MarkerExists() => valueName == InstallerMaintenanceGate.MaintenanceMarkerValueName
                ? InstallerMaintenanceGate.IsMaintenanceMarkerSetInRegistry(registryHive, registryPath)
                : InstallerMaintenanceGate.IsRecoveryMarkerSetInRegistry(registryHive, registryPath);

            InstallerMaintenanceGate restartedServiceGate = valueName == InstallerMaintenanceGate.MaintenanceMarkerValueName
                ? new InstallerMaintenanceGate(MarkerExists, () => false)
                : new InstallerMaintenanceGate(() => false, MarkerExists);

            Assert.True(MarkerExists());
            Assert.True(restartedServiceGate.IsHeldByInstaller());
        });
    }

    [Theory]
    [InlineData("invalid", RegistryValueKind.String)]
    [InlineData("1", RegistryValueKind.DWord)]
    public void RegistryReader_FailsClosedForUnexpectedMarkerFormat(string marker, RegistryValueKind valueKind)
    {
        WithTemporaryRegistryPath((registryHive, registryPath) =>
        {
            object registryValue = valueKind == RegistryValueKind.DWord ? 1 : marker;
            SetMarker(registryHive, registryPath, InstallerMaintenanceGate.MaintenanceMarkerValueName, registryValue, valueKind);

            Assert.True(InstallerMaintenanceGate.IsMaintenanceMarkerSetInRegistry(registryHive, registryPath));
        });
    }

    private static void WithTemporaryRegistryPath(Action<RegistryHive, string> action)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RegistryHive registryHive = RegistryHive.CurrentUser;
        var registryPath = $@"Software\KGTA.Alert.Tests\InstallerMaintenanceMarker\{Guid.NewGuid():N}";
        try
        {
            action(registryHive, registryPath);
        }
        finally
        {
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
        }
    }

    private static void SetMarker(
        RegistryHive registryHive,
        string registryPath,
        string valueName,
        object value,
        RegistryValueKind valueKind)
    {
        using var baseKey = RegistryKey.OpenBaseKey(registryHive, RegistryView.Registry64);
        using RegistryKey markerKey = baseKey.CreateSubKey(registryPath)!;
        markerKey.SetValue(valueName, value, valueKind);
    }
}
