using System.Security.AccessControl;
using System.Security.Principal;
using Watcher.EdgeAgent.AudioWorker;

namespace Watcher.EdgeAgent.Service.IntegrationTests;

public sealed class AudioWorkerSecurityTests
{
    [Fact]
    public void PipeAcl_AllowsOnlyServiceReadWriteWithoutCreatingServerInstances()
    {
        SecurityIdentifier serviceSid = new("S-1-5-80-111-222-333-444-555");

        var accessSddl = AudioWorkerPipeServer.GetPipeAccessSddl(serviceSid);

        Assert.Contains(serviceSid.Value, accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;WD)", accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU)", accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain("0x4", accessSddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(4, 5, false)]
    [InlineData(0, 0, false)]
    [InlineData(4, uint.MaxValue, false)]
    public void SessionValidation_UsesOnlyActiveConsoleSession(int workerSessionId, uint activeSessionId, bool expected)
    {
        Assert.Equal(expected, AudioWorkerPipeServer.IsAllowedSession(workerSessionId, activeSessionId));
    }
}
