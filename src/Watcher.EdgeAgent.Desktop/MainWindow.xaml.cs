using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Watcher.EdgeAgent.Contracts;

namespace Watcher.EdgeAgent.Desktop;

public partial class MainWindow : Window
{
    private const string PipeName = "KGTA.Alert.EdgeAgent.Status.v1";
    private const int MaximumFrameBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
        _refreshTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e) => _refreshTimer.Stop();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            var request = JsonSerializer.SerializeToUtf8Bytes(new EdgeAgentPipeRequest(1, "get_status"), JsonOptions);
            await WriteFrameAsync(pipe, request, timeout.Token);
            var responseBytes = await ReadFrameAsync(pipe, timeout.Token);
            EdgeAgentPipeResponse? response = JsonSerializer.Deserialize<EdgeAgentPipeResponse>(responseBytes, JsonOptions);
            if (response is not { SchemaVersion: 1, Succeeded: true, Status: not null })
            {
                ShowUnavailable(response?.DiagnosticCode ?? "invalid_response");
                return;
            }

            Render(response.Status);
        }
        catch (UnauthorizedAccessException)
        {
            ShowAccessDenied();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            ShowUnavailable("service_unavailable");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            ShowUnavailable("invalid_response");
        }
        finally { _refreshing = false; }
    }

    private void Render(EdgeAgentStatus status)
    {
        ConnectionText.Text = status.ConnectionState switch
        {
            EdgeAgentConnectionState.Streaming => "Підключено до сервера",
            EdgeAgentConnectionState.Connecting or EdgeAgentConnectionState.Reconnecting => "Підключення до сервера…",
            EdgeAgentConnectionState.Degraded => "Потрібна перевірка вузла",
            EdgeAgentConnectionState.Stopped => "Служба зупинена",
            _ => "Служба запускається"
        };
        StateIndicator.Fill = status.ConnectionState switch
        {
            EdgeAgentConnectionState.Streaming => new SolidColorBrush(Color.FromRgb(34, 150, 91)),
            EdgeAgentConnectionState.Degraded or EdgeAgentConnectionState.Stopped => new SolidColorBrush(Color.FromRgb(197, 55, 55)),
            _ => new SolidColorBrush(Color.FromRgb(224, 160, 35))
        };
        DeviceText.Text = $"Вузол: {status.DeviceId?.ToString("D") ?? "не визначено"} · Локація: {status.LocationId?.ToString("D") ?? "не визначена"}";
        AudioText.Text = $"Аудіовихід: {status.AudioState} · {status.AudioEndpointId ?? "не налаштовано"}";
        CommandText.Text = status.LastCommandId is null
            ? "Остання команда: немає"
            : $"Остання команда: {status.LastCommandState} · № {status.LastSequence} · {status.LastCommandId:D}";
        QueueText.Text = $"Квитанцій у черзі: {status.PendingReceiptCount}";
        DiagnosticText.Text = status.DiagnosticCode is null ? "Діагностика: немає" : $"Діагностика: {status.DiagnosticCode}";
        UpdatedText.Text = $"Оновлено {status.ObservedAtUtc.ToLocalTime():HH:mm:ss}";
    }

    private void ShowUnavailable(string diagnostic)
    {
        ConnectionText.Text = "Служба недоступна";
        StateIndicator.Fill = new SolidColorBrush(Color.FromRgb(197, 55, 55));
        DeviceText.Text = "Вузол: —";
        AudioText.Text = "Аудіовихід: —";
        CommandText.Text = "Остання команда: —";
        QueueText.Text = "Квитанцій у черзі: —";
        DiagnosticText.Text = $"Діагностика: {diagnostic}";
        UpdatedText.Text = $"Перевірено {DateTimeOffset.Now:HH:mm:ss}";
    }

    private void ShowAccessDenied()
    {
        ShowUnavailable("access_denied");
        DiagnosticText.Text = "Доступ до панелі відсутній. Попросіть адміністратора додати ваш обліковий запис до групи KGTA Alert Operators.";
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        if (payload.Length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException();
        var response = new byte[length];
        await ReadExactlyAsync(stream, response, token);
        return response;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken token)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
