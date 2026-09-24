using System.Diagnostics.Tracing;
using Azure.Core.Diagnostics;

namespace AzureDash;

/// <summary>
/// AZURE_DEBUG=true: forwards Azure SDK EventSource output (HTTP requests, retries, token acquisition) to logging.
/// Azure.Core redacts Authorization and other non-allow-listed headers; request/response bodies are not logged.
/// </summary>
public sealed class AzureSdkLogging(ILoggerFactory loggers) : IDisposable
{
    private AzureEventSourceListener? _listener;

    public void Start()
    {
        var log = loggers.CreateLogger("Azure.Sdk");
        _listener ??= new AzureEventSourceListener(
            (e, message) => log.LogInformation("{Source}/{Event}: {Message}", e.EventSource.Name, e.EventName, message),
            EventLevel.Verbose);
    }

    public void Dispose() => _listener?.Dispose();
}
