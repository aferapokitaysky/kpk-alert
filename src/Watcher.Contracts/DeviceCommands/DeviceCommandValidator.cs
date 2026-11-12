namespace Watcher.Contracts.DeviceCommands;

public static class DeviceCommandValidator
{
    public const int CurrentSchemaVersion = 1;
    public const int ReplaceSchemaVersion = 2;
    public const int MaximumPlaybackDurationSeconds = 900;

    public static string? Validate(CommandDispatch command)
    {
        if (command.CommandId == Guid.Empty || command.DeviceId == Guid.Empty ||
            command.LocationId == Guid.Empty || command.Sequence < 1)
        {
            return "invalid_identity";
        }

        if (command.IssuedAtUtc.Offset != TimeSpan.Zero ||
            command.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            command.ExpiresAtUtc <= command.IssuedAtUtc)
        {
            return "invalid_time";
        }

        return command.Action switch
        {
            DeviceCommandAction.Start when
                command.SchemaVersion == CurrentSchemaVersion &&
                command.TargetCommandId is null &&
                IsPresetCode(command.PresetCode) &&
                IsSha256(command.ManifestHash) &&
                command.MaxPlaybackDurationSeconds is > 0 and <= MaximumPlaybackDurationSeconds => null,
            DeviceCommandAction.Stop when
                command.SchemaVersion == CurrentSchemaVersion &&
                command.TargetCommandId is { } target && target != Guid.Empty &&
                command.PresetCode is null && command.ManifestHash is null &&
                command.MaxPlaybackDurationSeconds == 0 => null,
            DeviceCommandAction.Replace when
                command.SchemaVersion == ReplaceSchemaVersion &&
                command.TargetCommandId is { } episodeAnchor && episodeAnchor != Guid.Empty &&
                IsPresetCode(command.PresetCode) &&
                IsSha256(command.ManifestHash) &&
                command.MaxPlaybackDurationSeconds is > 0 and <= MaximumPlaybackDurationSeconds => null,
            _ => "invalid_action_payload"
        };
    }

    public static string? Validate(CommandReceipt receipt)
    {
        if (receipt.SchemaVersion is not (CurrentSchemaVersion or ReplaceSchemaVersion) ||
            receipt.EventId == Guid.Empty || receipt.CommandId == Guid.Empty ||
            receipt.Sequence < 1 || !Enum.IsDefined(receipt.State))
        {
            return "invalid_identity";
        }

        if (receipt.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            return "invalid_time";
        }

        var needsReason = receipt.State is DeviceCommandState.Rejected or
            DeviceCommandState.Failed or DeviceCommandState.Expired;
        if (needsReason != (receipt.DiagnosticCode is not null) ||
            (receipt.DiagnosticCode is not null && !IsDiagnosticCode(receipt.DiagnosticCode)))
        {
            return "invalid_diagnostic_code";
        }

        return null;
    }

    private static bool IsPresetCode(string? code) =>
        code is { Length: > 0 and <= 80 } &&
        code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool IsSha256(string? hash) =>
        hash is { Length: 64 } &&
        hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool IsDiagnosticCode(string code) =>
        code is { Length: > 0 and <= 64 } &&
        code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
