using System.Buffers.Binary;
using System.ComponentModel;
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
    private const int MaximumRequestFrameBytes = 4096;
    private const int MaximumResponseFrameBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _windowLifetime = new();
    private IReadOnlyList<AudioOutputChoice> _audioOutputs = Array.Empty<AudioOutputChoice>();
    private string? _configuredAudioEndpointId;
    private bool _refreshing;
    private bool _loadingAudioOutputs;
    private bool _audioOperationInProgress;
    private bool _allowClose;
    private Task? _activeAudioOperation;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
        await RefreshAudioOutputsAsync();
        _refreshTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _windowLifetime.Cancel();
        _windowLifetime.Dispose();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _windowLifetime.Cancel();
        if (_activeAudioOperation is not null)
        {
            await _activeAudioOperation;
        }

        _allowClose = true;
        Close();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void RefreshAudioOutputs_Click(object sender, RoutedEventArgs e) =>
        await RefreshAudioOutputsAsync(preserveSelection: true);

    private void AudioOutput_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingAudioOutputs || AudioOutputComboBox.SelectedItem is not AudioOutputChoice selected) return;
        AudioOutputMessage.Text = selected.Id == _configuredAudioEndpointId
            ? "Вибрано поточний вихід служби."
            : "Перевірте вихід, потім застосуйте його для тривожних сигналів.";
        UpdateAudioButtons();
    }

    private async void TestTone_Click(object sender, RoutedEventArgs e) =>
        await StartAudioOperationAsync(AudioTestSound.Tone, applySelection: false);

    private async void TestChime_Click(object sender, RoutedEventArgs e) =>
        await StartAudioOperationAsync(AudioTestSound.Chime, applySelection: false);

    private async void ApplyAudioOutput_Click(object sender, RoutedEventArgs e) =>
        await StartAudioOperationAsync(null, applySelection: true);

    private async Task RefreshAudioOutputsAsync(bool preserveSelection = false)
    {
        try
        {
            EdgeAgentPipeResponse response = await SendPipeRequestAsync(
                new EdgeAgentPipeRequest(1, "get_audio_outputs"), _windowLifetime.Token);
            if (!response.Succeeded || response.AudioEndpoints is null)
            {
                ShowAudioOutputUnavailable(DescribeAudioInventoryFailure(response.DiagnosticCode));
                return;
            }

            _audioOutputs = response.AudioEndpoints.Select(endpoint =>
                new AudioOutputChoice(endpoint.Id, endpoint.Name, endpoint.IsDefault)).ToArray();
            AudioOutputPlaceholder.Text = _audioOutputs.Count == 0
                ? "Служба працює, але Windows не повідомила активних аудіовиходів. Перевірте аудіопристрої та оновіть список."
                : string.Empty;
            AudioOutputPlaceholder.Visibility = _audioOutputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (response.Status is { } status)
            {
                _configuredAudioEndpointId = status.AudioEndpointId;
                Render(status);
            }
            var preferredEndpointId = preserveSelection && AudioOutputComboBox.SelectedItem is AudioOutputChoice selected
                ? selected.Id
                : _configuredAudioEndpointId;
            _loadingAudioOutputs = true;
            AudioOutputComboBox.ItemsSource = _audioOutputs;
            AudioOutputComboBox.DisplayMemberPath = nameof(AudioOutputChoice.DisplayName);
            AudioOutputComboBox.SelectedItem = _audioOutputs.FirstOrDefault(output => output.Id == preferredEndpointId)
                ?? _audioOutputs.FirstOrDefault(output => output.IsDefault)
                ?? _audioOutputs.FirstOrDefault();
            _loadingAudioOutputs = false;
            AudioOutputMessage.Text = _audioOutputs.Count == 0
                ? AudioOutputPlaceholder.Text
                : AudioOutputComboBox.SelectedItem is AudioOutputChoice selectedOutput &&
                  selectedOutput.Id != _configuredAudioEndpointId
                    ? "Вибрано інший вихід. Перевірте його, потім застосуйте для тривожних сигналів."
                    : "Тест і тривожні сигнали відтворюються службою через поточний вихід.";
            UpdateAudioButtons();
        }
        catch (UnauthorizedAccessException)
        {
            ShowAudioOutputUnavailable(
                "Обліковий запис не має доступу до служби. Адміністратор має додати його до групи KGTA Alert Operators.");
        }
        catch (IOException)
        {
            ShowAudioOutputUnavailable(
                "Служба KGTA Alert Edge Agent не встановлена або не запущена. Встановіть і запустіть службу, потім оновіть список.");
        }
        catch (Exception exception) when (exception is InvalidDataException or TimeoutException or
                                          OperationCanceledException or JsonException)
        {
            ShowAudioOutputUnavailable(
                "Служба не повернула перелік аудіовиходів. Перевірте її стан і повторіть оновлення.");
        }
    }

    private void ShowAudioOutputUnavailable(string message)
    {
        _audioOutputs = Array.Empty<AudioOutputChoice>();
        _loadingAudioOutputs = true;
        AudioOutputComboBox.ItemsSource = _audioOutputs;
        AudioOutputComboBox.SelectedItem = null;
        _loadingAudioOutputs = false;
        AudioOutputPlaceholder.Text = message;
        AudioOutputPlaceholder.Visibility = Visibility.Visible;
        AudioOutputMessage.Text = message;
        UpdateAudioButtons();
    }

    private async Task StartAudioOperationAsync(AudioTestSound? testSound, bool applySelection)
    {
        if (_activeAudioOperation is not null) return;
        _refreshTimer.Stop();
        Task operation = RunAudioOperationAsync(testSound, applySelection);
        _activeAudioOperation = operation;
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_activeAudioOperation, operation)) _activeAudioOperation = null;
        }
    }

    private async Task RunAudioOperationAsync(AudioTestSound? testSound, bool applySelection)
    {
        if (_audioOperationInProgress || AudioOutputComboBox.SelectedItem is not AudioOutputChoice selected) return;
        _audioOperationInProgress = true;
        UpdateAudioButtons();
        Guid? leaseId = null;
        string? finalMessage = null;
        try
        {
            AudioOutputMessage.Text = "Очікуємо завершення поточного відтворення…";
            EdgeAgentPipeResponse begin = await SendPipeRequestAsync(
                new EdgeAgentPipeRequest(1, "begin_audio_maintenance"), _windowLifetime.Token,
                TimeSpan.FromMinutes(16));
            if (!begin.Succeeded || begin.LeaseId is not { } acquiredLease)
            {
                finalMessage = DescribeAudioOperationFailure(begin.DiagnosticCode);
                AudioOutputMessage.Text = finalMessage;
                return;
            }

            leaseId = acquiredLease;
            EdgeAgentPipeRequest request = applySelection
                ? new EdgeAgentPipeRequest(1, "set_audio_output", leaseId, selected.Id)
                : new EdgeAgentPipeRequest(1, "test_audio_output", leaseId, selected.Id, testSound);
            EdgeAgentPipeResponse result = await SendPipeRequestAsync(request, _windowLifetime.Token);
            if (!result.Succeeded)
            {
                finalMessage = DescribeAudioOperationFailure(result.DiagnosticCode);
                AudioOutputMessage.Text = finalMessage;
                return;
            }

            if (applySelection)
            {
                _configuredAudioEndpointId = selected.Id;
                finalMessage = "Аудіовихід тривожних сигналів оновлено.";
                AudioOutputMessage.Text = finalMessage;
                if (result.Status is { } status)
                {
                    AudioText.Text = $"Аудіовихід: {status.AudioState} · {status.AudioEndpointId ?? "не налаштовано"}";
                }
            }
            else
            {
                finalMessage = "Тестовий звук завершено через аудіовихід служби.";
                AudioOutputMessage.Text = finalMessage;
            }
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            finalMessage = "Зв’язок зі службою перервано. Перевірте її стан і повторіть дію.";
            AudioOutputMessage.Text = finalMessage;
        }
        finally
        {
            if (leaseId is { } activeLease)
            {
                try
                {
                    await SendPipeRequestAsync(
                        new EdgeAgentPipeRequest(1, "release_maintenance", activeLease), CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or JsonException)
                {
                    finalMessage = "Не вдалося підтвердити завершення обслуговування. Перевірте стан служби.";
                    AudioOutputMessage.Text = finalMessage;
                }
            }

            if (!_windowLifetime.IsCancellationRequested)
            {
                await RefreshStatusAsync();
                await RefreshAudioOutputsAsync(preserveSelection: !applySelection);
            }

            if (finalMessage is not null) AudioOutputMessage.Text = finalMessage;
            _audioOperationInProgress = false;
            UpdateAudioButtons();
            if (!_windowLifetime.IsCancellationRequested)
            {
                _refreshTimer.Start();
            }
        }
    }

    private void UpdateAudioButtons()
    {
        var hasSelection = AudioOutputComboBox.SelectedItem is AudioOutputChoice;
        var canOperate = hasSelection && !_audioOperationInProgress && !_windowLifetime.IsCancellationRequested;
        AudioOutputComboBox.IsEnabled = _audioOutputs.Count > 0 && !_audioOperationInProgress;
        AudioOutputPlaceholder.Visibility = _audioOutputs.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        TestToneButton.IsEnabled = canOperate;
        TestChimeButton.IsEnabled = canOperate;
        RefreshAudioOutputsButton.IsEnabled = !_audioOperationInProgress;
        RefreshStatusButton.IsEnabled = !_audioOperationInProgress;
        ApplyAudioOutputButton.IsEnabled = canOperate &&
            AudioOutputComboBox.SelectedItem is AudioOutputChoice selected && selected.Id != _configuredAudioEndpointId;
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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
            EdgeAgentConnectionState.NotConfigured => "Виберіть аудіовихід для налаштування вузла",
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
        StartedText.Text = status.LastStartedAtUtc is { } lastStartedAtUtc
            ? $"Службу запущено: {lastStartedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}"
            : "Час запуску служби недоступний";
        var endpointName = _audioOutputs.FirstOrDefault(output => output.Id == status.AudioEndpointId)?.Name;
        AudioText.Text = $"Аудіовихід: {status.AudioState} · {endpointName ?? status.AudioEndpointId ?? "не налаштовано"}";
        CommandText.Text = status.LastCommandId is null
            ? "Остання команда: немає"
            : $"Остання команда: {status.LastCommandState} · № {status.LastSequence} · {status.LastCommandId:D}";
        QueueText.Text = $"Квитанцій у черзі: {status.PendingReceiptCount}";
        DiagnosticText.Text = FormatDiagnostic(status.DiagnosticCode);
        UpdatedText.Text = $"Оновлено {status.ObservedAtUtc.ToLocalTime():HH:mm:ss}";
    }

    private void ShowUnavailable(string diagnostic)
    {
        ConnectionText.Text = "Служба недоступна";
        StateIndicator.Fill = new SolidColorBrush(Color.FromRgb(197, 55, 55));
        DeviceText.Text = "Вузол: —";
        StartedText.Text = "Час запуску служби: —";
        AudioText.Text = "Аудіовихід: —";
        CommandText.Text = "Остання команда: —";
        QueueText.Text = "Квитанцій у черзі: —";
        DiagnosticText.Text = FormatDiagnostic(diagnostic);
        UpdatedText.Text = $"Перевірено {DateTimeOffset.Now:HH:mm:ss}";
    }

    private void ShowAccessDenied()
    {
        ShowUnavailable("access_denied");
        DiagnosticText.Text = "Доступ до панелі відсутній. Попросіть адміністратора додати ваш обліковий запис до групи KGTA Alert Operators.";
    }

    private static string FormatDiagnostic(string? diagnosticCode)
    {
        EdgeAgentDiagnostic diagnostic = EdgeAgentDiagnosticCatalog.Describe(diagnosticCode);
        var technicalCode = IsSafeDiagnosticCode(diagnosticCode) ? $"\nКод: {diagnosticCode}" : string.Empty;
        var recommendedAction = string.IsNullOrWhiteSpace(diagnostic.RecommendedAction)
            ? string.Empty
            : $"\nЩо зробити: {diagnostic.RecommendedAction}";

        return $"Діагностика: {diagnostic.Message}{recommendedAction}{technicalCode}";
    }

    private static string DescribeAudioOperationFailure(string? diagnosticCode) => diagnosticCode switch
    {
        "maintenance_access_denied" or "audio_output_access_denied" =>
            "Для зміни виходу й тесту звуку запустіть панель від імені адміністратора.",
        "maintenance_already_active" =>
            "Інша операція обслуговування вже виконується. Спробуйте пізніше.",
        "maintenance_drain_timeout" =>
            "Сигнал ще відтворюється. Після його завершення повторіть дію.",
        "maintenance_lease_invalid" =>
            "Операцію обслуговування завершено або її термін минув. Повторіть дію.",
        "audio_output_unavailable" =>
            "Вибраний вихід недоступний службі. Оновіть список пристроїв.",
        "audio_output_invalid" =>
            "Служба відхилила вибраний пристрій. Оновіть список і виберіть вихід зі списку.",
        "audio_output_save_failed" =>
            "Не вдалося зберегти вихід служби. Зверніться до адміністратора вузла.",
        "audio_output_enumeration_failed" =>
            "Служба не змогла прочитати список аудіопристроїв. Перевірте Windows Audio та повторіть спробу.",
        "audio_output_configuration_invalid" =>
            "Збережений вихід служби пошкоджений. Виберіть пристрій зі списку та застосуйте його повторно.",
        "audio_test_failed" =>
            "Служба не змогла відтворити тестовий звук на цьому виході.",
        _ => FormatDiagnostic(diagnosticCode)
    };

    private static string DescribeAudioInventoryFailure(string? diagnosticCode) => diagnosticCode switch
    {
        "audio_output_unavailable" =>
            "Служба запущена, але перелік аудіовиходів недоступний. Перевірте налаштування служби.",
        "audio_output_enumeration_failed" =>
            "Служба не змогла отримати перелік аудіовиходів Windows. Перевірте аудіопристрої та оновіть список.",
        _ => DescribeAudioOperationFailure(diagnosticCode)
    };

    private static async Task<EdgeAgentPipeResponse> SendPipeRequestAsync(
        EdgeAgentPipeRequest request,
        CancellationToken cancellationToken,
        TimeSpan? timeoutDuration = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration ?? TimeSpan.FromSeconds(15));
        using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        await WriteFrameAsync(pipe, payload, timeout.Token);
        if (request.Operation is "begin_maintenance" or "begin_audio_maintenance")
        {
            await pipe.WriteAsync(new byte[] { 0xA5 }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }

        var responseBytes = await ReadFrameAsync(pipe, timeout.Token);
        EdgeAgentPipeResponse? response = JsonSerializer.Deserialize<EdgeAgentPipeResponse>(responseBytes, JsonOptions);
        if (response is not { SchemaVersion: 1 }) throw new InvalidDataException();
        return response;
    }

    private static bool IsSafeDiagnosticCode(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        if (payload.Length is <= 0 or > MaximumRequestFrameBytes) throw new InvalidDataException();
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
        if (length is <= 0 or > MaximumResponseFrameBytes) throw new InvalidDataException();
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

    private sealed record AudioOutputChoice(string Id, string Name, bool IsDefault)
    {
        public string DisplayName => IsDefault ? $"{Name} (типовий для служби)" : Name;
    }
}
