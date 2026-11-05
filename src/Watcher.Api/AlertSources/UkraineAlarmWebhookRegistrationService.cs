using Microsoft.Extensions.Options;

namespace Watcher.Api.AlertSources;

public sealed class UkraineAlarmWebhookRegistrationService(
    IUkraineAlarmWebhookRegistrar client,
    IOptions<UkraineAlarmOptions> options)
{
    public Uri GetCallbackUrl() => CreateCallbackUrl(options.Value);

    public Task RegisterAsync(CancellationToken cancellationToken) =>
        RegisterAsync(GetCallbackUrl(), cancellationToken);

    public Task RegisterAsync(Uri callbackUrl, CancellationToken cancellationToken) =>
        client.RegisterWebhookAsync(callbackUrl, cancellationToken);

    public Task UpdateAsync(CancellationToken cancellationToken) =>
        UpdateAsync(GetCallbackUrl(), cancellationToken);

    public Task UpdateAsync(Uri callbackUrl, CancellationToken cancellationToken) =>
        client.UpdateWebhookAsync(callbackUrl, cancellationToken);

    private static Uri CreateCallbackUrl(UkraineAlarmOptions settings)
    {
        if (!Uri.TryCreate(settings.PublicBaseUrl, UriKind.Absolute, out Uri? publicBaseUri))
        {
            throw new InvalidOperationException("Потрібно налаштувати публічну HTTPS-адресу сервера UkraineAlarm.");
        }

        try
        {
            return UkraineAlarmWebhookUrl.Create(publicBaseUri);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Конфігурація webhook UkraineAlarm некоректна.", exception);
        }
    }
}

public static class UkraineAlarmWebhookUrl
{
    public static Uri Create(Uri publicBaseUri)
    {
        ArgumentNullException.ThrowIfNull(publicBaseUri);
        if (!publicBaseUri.IsAbsoluteUri || publicBaseUri.Scheme != Uri.UriSchemeHttps ||
            publicBaseUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(publicBaseUri.UserInfo) ||
            !string.IsNullOrEmpty(publicBaseUri.Query) ||
            !string.IsNullOrEmpty(publicBaseUri.Fragment))
        {
            throw new ArgumentException("Публічна адреса сервера має бути HTTPS origin без облікових даних.",
                nameof(publicBaseUri));
        }

        return new Uri(publicBaseUri, "api/v1/alert-sources/ukrainealarm/webhook");
    }
}
