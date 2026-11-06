namespace Watcher.EdgeAgent.Core;

public sealed class EdgeAgentOptions
{
    public const int StartCommandReceiptCapacity = 3;

    public required Uri BaseAddress { get; init; }
    public required string IdentityPath { get; init; }
    public required string WebSocketPath { get; init; }
    public required string ReceiptPath { get; init; }
    public required string DatabasePath { get; init; }
    public required string AudioRootDirectory { get; init; }
    public string? ClientCertificateThumbprint { get; init; }
    public string? AudioEndpointId { get; init; }
    public int MaximumClockSkewSeconds { get; init; } = 120;
    public int MaximumWebSocketMessageBytes { get; init; } = 32 * 1024;
    public int MaximumReceiptQueue { get; init; } = 10_000;
    public int ReconnectInitialDelaySeconds { get; init; } = 1;
    public int ReconnectMaximumDelaySeconds { get; init; } = 30;
    public int ReceiptRetryMaximumDelaySeconds { get; init; } = 60;

    public string? Validate()
    {
        if (BaseAddress is null || !BaseAddress.IsAbsoluteUri || BaseAddress.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(BaseAddress.UserInfo) || !string.IsNullOrEmpty(BaseAddress.Query) ||
            !string.IsNullOrEmpty(BaseAddress.Fragment))
        {
            return "invalid_server_url";
        }

        if (!IsSafePath(IdentityPath) || !IsSafePath(WebSocketPath) || !IsSafePath(ReceiptPath) ||
            !string.Equals(IdentityPath, "/api/v1/device/identity", StringComparison.Ordinal) ||
            !string.Equals(WebSocketPath, "/api/v1/device/commands/stream", StringComparison.Ordinal) ||
            !string.Equals(ReceiptPath, "/api/v1/device/command-receipts", StringComparison.Ordinal))
        {
            return "invalid_server_path";
        }

        if (string.IsNullOrWhiteSpace(DatabasePath) || string.IsNullOrWhiteSpace(AudioRootDirectory) ||
            MaximumClockSkewSeconds is < 0 or > 600 ||
            MaximumWebSocketMessageBytes is < 1024 or > 32 * 1024 ||
            MaximumReceiptQueue is < StartCommandReceiptCapacity or > 100_000 ||
            ReconnectInitialDelaySeconds is < 1 or > 30 ||
            ReconnectMaximumDelaySeconds < ReconnectInitialDelaySeconds ||
            ReconnectMaximumDelaySeconds > 300 || ReceiptRetryMaximumDelaySeconds is < 1 or > 3600)
        {
            return "invalid_configuration";
        }

        return null;
    }

    private static bool IsSafePath(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !value.Contains('?') &&
        !value.Contains('#');
}
