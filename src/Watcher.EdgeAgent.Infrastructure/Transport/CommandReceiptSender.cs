using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Transport;

public sealed class CommandReceiptSender(
    EdgeAgentOptions options,
    IEdgeAgentStore store,
    HttpClient httpClient,
    IEdgeAgentClock clock,
    Func<string, Exception?, ValueTask>? reportDiagnostic = null)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PendingCommandReceipt? receipt = await store.GetNextReceiptAsync(clock.UtcNow, cancellationToken);
            if (receipt is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            ReceiptSendResult result = await SendOnceAsync(receipt, cancellationToken);
            if (result.Disposition == ReceiptSendDisposition.Delivered)
            {
                await store.MarkReceiptDeliveredAsync(receipt.EventId, cancellationToken);
                continue;
            }

            if (result.Disposition == ReceiptSendDisposition.PermanentFailure)
            {
                await ReportAsync(result.DiagnosticCode ?? "receipt_rejected", null);
                await store.ScheduleReceiptRetryAsync(
                    receipt.EventId,
                    DateTimeOffset.MaxValue,
                    cancellationToken);
                continue;
            }

            TimeSpan retryDelay = GetRetryDelay(
                receipt.AttemptCount,
                TimeSpan.FromSeconds(Math.Min(30, options.ReconnectInitialDelaySeconds)),
                TimeSpan.FromSeconds(options.ReceiptRetryMaximumDelaySeconds),
                result.RetryAfter,
                Random.Shared.NextDouble());
            await store.ScheduleReceiptRetryAsync(receipt.EventId, clock.UtcNow + retryDelay, cancellationToken);
        }
    }

    public async Task<ReceiptSendResult> SendOnceAsync(
        PendingCommandReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        CommandReceipt contract = new(
            1,
            receipt.EventId,
            receipt.CommandId,
            receipt.Sequence,
            receipt.State,
            receipt.OccurredAtUtc,
            receipt.DiagnosticCode);

        try
        {
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(RequestTimeout);
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                new Uri(options.BaseAddress, options.ReceiptPath),
                contract,
                JsonOptions,
                attemptTimeout.Token);

            if (response.StatusCode is HttpStatusCode.Accepted ||
                response.StatusCode is HttpStatusCode.OK && await IsDuplicateResponseAsync(response, cancellationToken))
            {
                return new ReceiptSendResult(ReceiptSendDisposition.Delivered, null, null);
            }

            if (IsTransient(response.StatusCode))
            {
                return new ReceiptSendResult(
                    ReceiptSendDisposition.Retry,
                    "receipt_transport_retry",
                    GetRetryAfter(response, clock.UtcNow));
            }

            return new ReceiptSendResult(
                ReceiptSendDisposition.PermanentFailure,
                response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "receipt_contract_rejected",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "device_certificate_rejected",
                    HttpStatusCode.NotFound => "receipt_device_or_command_unknown",
                    HttpStatusCode.Conflict => "receipt_state_conflict",
                    _ => "receipt_request_rejected"
                },
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReceiptSendResult(ReceiptSendDisposition.Retry, "receipt_request_timeout", null);
        }
        catch (HttpRequestException)
        {
            return new ReceiptSendResult(ReceiptSendDisposition.Retry, "receipt_transport_unavailable", null);
        }
    }

    internal static TimeSpan GetRetryDelay(
        int attempt,
        TimeSpan initial,
        TimeSpan maximum,
        TimeSpan? retryAfter,
        double jitterFactor)
    {
        TimeSpan cap = maximum > TimeSpan.Zero ? maximum : TimeSpan.FromSeconds(1);
        if (retryAfter is { } serverDelay && serverDelay >= TimeSpan.Zero)
        {
            return serverDelay > cap ? cap : serverDelay;
        }

        var exponent = Math.Clamp(attempt, 0, 30);
        var rawMilliseconds = Math.Min(cap.TotalMilliseconds, initial.TotalMilliseconds * Math.Pow(2, exponent));
        var jitter = Math.Clamp(jitterFactor, 0.75, 1.25);
        return TimeSpan.FromMilliseconds(Math.Min(cap.TotalMilliseconds, rawMilliseconds * jitter));
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response, DateTimeOffset nowUtc)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            TimeSpan delay = date - nowUtc;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    private static async Task<bool> IsDuplicateResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            DuplicateReceiptResponse? body = await response.Content.ReadFromJsonAsync<DuplicateReceiptResponse>(
                JsonOptions, cancellationToken);
            return body?.Duplicate == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async ValueTask ReportAsync(string code, Exception? exception)
    {
        if (reportDiagnostic is not null)
        {
            await reportDiagnostic(code, exception);
        }
    }

    private sealed record DuplicateReceiptResponse(bool Duplicate);
}

public enum ReceiptSendDisposition
{
    Delivered,
    Retry,
    PermanentFailure
}

public sealed record ReceiptSendResult(
    ReceiptSendDisposition Disposition,
    string? DiagnosticCode,
    TimeSpan? RetryAfter);
