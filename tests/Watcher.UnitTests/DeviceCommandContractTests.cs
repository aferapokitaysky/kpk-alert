using System.Text.Json;
using Watcher.Contracts.DeviceCommands;

namespace Watcher.UnitTests;

public sealed class DeviceCommandContractTests
{
    [Fact]
    public void Start_requires_manifest_duration_and_no_stop_target()
    {
        CommandDispatch valid = ValidStart();

        Assert.Null(DeviceCommandValidator.Validate(valid));
        Assert.Equal("invalid_action_payload", DeviceCommandValidator.Validate(valid with { ManifestHash = "bad" }));
        Assert.Equal("invalid_action_payload", DeviceCommandValidator.Validate(valid with { MaxPlaybackDurationSeconds = 0 }));
        Assert.Equal("invalid_action_payload", DeviceCommandValidator.Validate(valid with { TargetCommandId = Guid.NewGuid() }));
    }

    [Fact]
    public void Stop_requires_target_and_forbids_playback_fields()
    {
        CommandDispatch valid = ValidStart() with
        {
            Action = DeviceCommandAction.Stop,
            TargetCommandId = Guid.NewGuid(),
            PresetCode = null,
            ManifestHash = null,
            MaxPlaybackDurationSeconds = 0
        };

        Assert.Null(DeviceCommandValidator.Validate(valid));
        Assert.Equal("invalid_action_payload", DeviceCommandValidator.Validate(valid with { TargetCommandId = null }));
        Assert.Equal("invalid_action_payload", DeviceCommandValidator.Validate(valid with { PresetCode = "air_raid" }));
    }

    [Fact]
    public void Replace_requires_v2_schema_episode_anchor_and_validated_audio()
    {
        CommandDispatch valid = ValidStart() with
        {
            SchemaVersion = DeviceCommandValidator.ReplaceSchemaVersion,
            Action = DeviceCommandAction.Replace,
            TargetCommandId = Guid.NewGuid()
        };

        Assert.Null(DeviceCommandValidator.Validate(valid));
        Assert.Equal("invalid_action_payload", DeviceCommandValidator.Validate(valid with
        {
            SchemaVersion = DeviceCommandValidator.CurrentSchemaVersion
        }));
        Assert.Equal("invalid_action_payload", DeviceCommandValidator.Validate(valid with
        {
            TargetCommandId = Guid.Empty
        }));
        Assert.Equal("invalid_action_payload", DeviceCommandValidator.Validate(valid with
        {
            ManifestHash = null
        }));
    }

    [Fact]
    public void Replace_dispatch_uses_explicit_v2_wire_action()
    {
        CommandDispatch dispatch = ValidStart() with
        {
            SchemaVersion = DeviceCommandValidator.ReplaceSchemaVersion,
            Action = DeviceCommandAction.Replace,
            TargetCommandId = Guid.NewGuid()
        };

        var json = JsonSerializer.Serialize(dispatch);

        Assert.Contains("\"schemaVersion\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"replace\"", json, StringComparison.Ordinal);
        Assert.Equal(dispatch, JsonSerializer.Deserialize<CommandDispatch>(json));
    }

    [Fact]
    public void Replacement_receipts_accept_v2_without_changing_v1_receipt_compatibility()
    {
        CommandReceipt receipt = new(DeviceCommandValidator.ReplaceSchemaVersion,
            Guid.NewGuid(), Guid.NewGuid(), 1, DeviceCommandState.Playing,
            new DateTimeOffset(2026, 11, 5, 9, 0, 1, TimeSpan.Zero), null);

        Assert.Null(DeviceCommandValidator.Validate(receipt));
        Assert.Null(DeviceCommandValidator.Validate(receipt with
        {
            SchemaVersion = DeviceCommandValidator.CurrentSchemaVersion
        }));
        Assert.Equal("invalid_identity", DeviceCommandValidator.Validate(receipt with
        {
            SchemaVersion = 3
        }));
    }

    [Fact]
    public void Dispatch_rejects_non_utc_expired_or_unsequenced_commands()
    {
        CommandDispatch valid = ValidStart();

        Assert.Equal("invalid_time", DeviceCommandValidator.Validate(valid with { ExpiresAtUtc = valid.IssuedAtUtc }));
        Assert.Equal("invalid_time", DeviceCommandValidator.Validate(valid with { IssuedAtUtc = valid.IssuedAtUtc.ToOffset(TimeSpan.FromHours(2)) }));
        Assert.Equal("invalid_identity", DeviceCommandValidator.Validate(valid with { Sequence = 0 }));
    }

    [Fact]
    public void Failed_receipt_requires_bounded_reason_code()
    {
        CommandReceipt receipt = new(1, Guid.NewGuid(), Guid.NewGuid(), 1,
            DeviceCommandState.Failed, new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero), "audio_missing");

        Assert.Null(DeviceCommandValidator.Validate(receipt));
        Assert.Equal("invalid_identity", DeviceCommandValidator.Validate(receipt with { State = (DeviceCommandState)100 }));
        Assert.Equal("invalid_diagnostic_code", DeviceCommandValidator.Validate(receipt with { DiagnosticCode = null }));
        Assert.Equal("invalid_diagnostic_code", DeviceCommandValidator.Validate(receipt with { DiagnosticCode = "stack trace" }));
    }

    [Fact]
    public void Dispatch_action_uses_stable_string_value()
    {
        DateTimeOffset issuedAt = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
        CommandDispatch dispatch = new(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            DeviceCommandAction.Start,
            "air_raid",
            new string('a', 64),
            issuedAt,
            issuedAt.AddMinutes(5),
            600,
            null);

        var json = JsonSerializer.Serialize(dispatch);
        CommandDispatch? restored = JsonSerializer.Deserialize<CommandDispatch>(json);

        Assert.Contains("\"action\":\"start\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maxPlaybackDurationSeconds\":600", json, StringComparison.Ordinal);
        Assert.Equal(dispatch, restored);
    }

    [Fact]
    public void Receipt_state_uses_stable_string_value()
    {
        CommandReceipt receipt = new(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            DeviceCommandState.Playing,
            new DateTimeOffset(2026, 11, 5, 9, 0, 1, TimeSpan.Zero),
            null);

        var json = JsonSerializer.Serialize(receipt);
        CommandReceipt? restored = JsonSerializer.Deserialize<CommandReceipt>(json);

        Assert.Contains("\"state\":\"playing\"", json, StringComparison.Ordinal);
        Assert.Equal(receipt, restored);
    }

    private static CommandDispatch ValidStart()
    {
        DateTimeOffset issuedAt = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
        return new CommandDispatch(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            DeviceCommandAction.Start, "air_raid", new string('a', 64), issuedAt,
            issuedAt.AddMinutes(1), 600, null);
    }
}
