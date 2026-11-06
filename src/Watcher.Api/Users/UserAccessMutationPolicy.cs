namespace Watcher.Api.Users;

public sealed record UserAccessMutationDecision(bool Allowed, string? RejectionCode);

public static class UserAccessMutationPolicy
{
    /// <summary>Перевіряє заборону само-деактивації, зняття власної ролі admin і втрати останнього адміністратора.</summary>
    public static UserAccessMutationDecision Evaluate(
        Guid actorUserId,
        Guid targetUserId,
        bool deactivatesAccount,
        bool removesGlobalAdministrator,
        int remainingActiveGlobalAdministrators)
    {
        if (removesGlobalAdministrator && remainingActiveGlobalAdministrators < 1)
        {
            return new(false, "last_global_administrator");
        }

        var isSelfMutation = actorUserId == targetUserId &&
                             (deactivatesAccount || removesGlobalAdministrator);

        return isSelfMutation
            ? new(false, "self_access_change")
            : new(true, null);
    }
}
