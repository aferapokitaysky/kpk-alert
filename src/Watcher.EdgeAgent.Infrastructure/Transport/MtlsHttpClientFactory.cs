using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Transport;

public static class MtlsHttpClientFactory
{
    public static HttpClient Create(EdgeAgentOptions options, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(certificate);

        HttpClientHandler handler = new();
        handler.ClientCertificates.Add(certificate);
        handler.CheckCertificateRevocationList = true;
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
