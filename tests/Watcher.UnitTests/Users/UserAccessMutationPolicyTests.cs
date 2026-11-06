using Watcher.Api.Users;

namespace Watcher.UnitTests.Users;

public sealed class UserAccessMutationPolicyTests
{
    [Fact]
    public void Keeps_at_least_one_active_global_administrator()
    {
        UserAccessMutationDecision decision = UserAccessMutationPolicy.Evaluate(
            Guid.NewGuid(), Guid.NewGuid(), false, true, 0);

        Assert.False(decision.Allowed);
        Assert.Equal("last_global_administrator", decision.RejectionCode);
    }

    [Fact]
    public void Rejects_self_deactivation_even_when_another_global_administrator_exists()
    {
        var administratorId = Guid.NewGuid();

        UserAccessMutationDecision decision = UserAccessMutationPolicy.Evaluate(
            administratorId, administratorId, true, false, 1);

        Assert.False(decision.Allowed);
        Assert.Equal("self_access_change", decision.RejectionCode);
    }

    [Fact]
    public void Allows_an_administrator_to_change_another_user_when_access_remains_valid()
    {
        UserAccessMutationDecision decision = UserAccessMutationPolicy.Evaluate(
            Guid.NewGuid(), Guid.NewGuid(), true, true, 1);

        Assert.True(decision.Allowed);
        Assert.Null(decision.RejectionCode);
    }
}
