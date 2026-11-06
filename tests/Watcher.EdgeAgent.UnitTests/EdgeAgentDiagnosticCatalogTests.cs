using Watcher.EdgeAgent.Contracts;

namespace Watcher.EdgeAgent.UnitTests;

public sealed class EdgeAgentDiagnosticCatalogTests
{
    public static TheoryData<string> ProducedDiagnosticCodes => new()
    {
        "agent_restart",
        "agent_start_failed",
        "agent_stopping",
        "audio_format_unsupported",
        "audio_output_busy",
        "audio_output_unavailable",
        "audio_start_failed",
        "audio_stop_timeout",
        "clock_skew",
        "command_contract_conflict",
        "command_expired",
        "command_id_conflict",
        "command_message_too_large",
        "command_message_type_invalid",
        "command_payload_invalid",
        "command_state_persistence_failed",
        "command_state_persistence_timeout",
        "device_certificate_rejected",
        "device_certificate_unavailable",
        "device_stream_closed",
        "device_transport_unavailable",
        "identity_request_failed",
        "identity_response_invalid",
        "identity_response_too_large",
        "invalid_action_payload",
        "invalid_configuration",
        "invalid_diagnostic_code",
        "invalid_identity",
        "invalid_response",
        "invalid_request",
        "invalid_server_path",
        "invalid_server_url",
        "invalid_time",
        "manifest_mismatch",
        "preset_missing",
        "receipt_contract_rejected",
        "receipt_device_or_command_unknown",
        "receipt_queue_full",
        "receipt_rejected",
        "receipt_request_rejected",
        "receipt_request_timeout",
        "receipt_state_conflict",
        "receipt_transport_retry",
        "receipt_transport_unavailable",
        "service_unavailable",
        "stale_sequence",
        "status_observer_failed",
        "stop_attempt_failed",
        "stop_target_pending",
        "wrong_device"
    };

    [Theory]
    [MemberData(nameof(ProducedDiagnosticCodes))]
    public void Every_produced_diagnostic_code_has_operator_guidance(string code)
    {
        EdgeAgentDiagnostic diagnostic = EdgeAgentDiagnosticCatalog.Describe(code);

        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.RecommendedAction));
        Assert.DoesNotContain("невідому проблему", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("device_certificate_unavailable")]
    [InlineData("audio_output_unavailable")]
    [InlineData("receipt_queue_full")]
    [InlineData("service_unavailable")]
    public void Known_code_has_actionable_operator_guidance(string code)
    {
        EdgeAgentDiagnostic diagnostic = EdgeAgentDiagnosticCatalog.Describe(code);

        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.RecommendedAction));
        Assert.DoesNotContain(code, diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown_external_value")]
    [InlineData("<script>alert(1)</script>")]
    public void Unknown_code_uses_safe_fallback(string code)
    {
        EdgeAgentDiagnostic diagnostic = EdgeAgentDiagnosticCatalog.Describe(code);

        Assert.Equal("Виявлено невідому проблему вузла.", diagnostic.Message);
        Assert.Equal("Зверніться до адміністратора та вкажіть код діагностики.", diagnostic.RecommendedAction);
        Assert.DoesNotContain("<script>", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_diagnostic_has_no_recommended_action(string? code)
    {
        EdgeAgentDiagnostic diagnostic = EdgeAgentDiagnosticCatalog.Describe(code);

        Assert.Equal("Діагностики немає.", diagnostic.Message);
        Assert.Empty(diagnostic.RecommendedAction);
    }
}
