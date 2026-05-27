using System.Diagnostics.Tracing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel.Tracing;

/// <summary>
/// Surfaces internal OpenTelemetry SDK diagnostics (including exporter failures) through the
/// AppHost's ILogger. Without this, OTLP exporter errors are silent and traces fail to reach
/// the dashboard without any visible signal.
/// </summary>
internal sealed class OpenTelemetryDiagnosticsListener : EventListener, IHostedService
{
    private readonly ILogger<OpenTelemetryDiagnosticsListener> _logger;
    private readonly List<EventSource> _pending = new();

    public OpenTelemetryDiagnosticsListener(ILogger<OpenTelemetryDiagnosticsListener> logger)
    {
        _logger = logger;
        // Subscribe to any already-created sources (constructor may run after some were created).
        lock (_pending)
        {
            foreach (var src in _pending)
            {
                EnableForOtel(src);
            }
            _pending.Clear();
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (_logger is null)
        {
            // Base class may invoke this before our constructor completes; buffer for later.
            lock (_pending)
            {
                _pending.Add(eventSource);
            }
            return;
        }
        EnableForOtel(eventSource);
    }

    private void EnableForOtel(EventSource src)
    {
        if (src.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
        {
            EnableEvents(src, EventLevel.Warning);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (_logger is null) return;
        var payload = eventData.Payload is null
            ? string.Empty
            : string.Join(" | ", eventData.Payload);
        _logger.LogWarning("[otel-sdk:{Source}] {Event} {Payload}",
            eventData.EventSource.Name, eventData.EventName, payload);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
