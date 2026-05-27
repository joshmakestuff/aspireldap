using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel.Tracing;

/// <summary>
/// Hosted service that runs an <see cref="OpenLdapLogParser"/> per
/// <see cref="OpenLdapResource"/> that has opted in via <c>WithOpenTelemetry()</c>.
/// </summary>
internal sealed class OpenLdapLogParserHost : BackgroundService
{
    private readonly DistributedApplicationModel _model;
    private readonly ResourceLoggerService _loggers;
    private readonly ILogger<OpenLdapLogParserHost> _logger;

    public OpenLdapLogParserHost(
        DistributedApplicationModel model,
        ResourceLoggerService loggers,
        ILogger<OpenLdapLogParserHost> logger)
    {
        _model = model;
        _loggers = loggers;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = _model.Resources
            .OfType<OpenLdapResource>()
            .Where(r => r.OpenTelemetryEnabled)
            .Select(r => RunForResourceAsync(r, stoppingToken))
            .ToArray();

        return tasks.Length == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    private async Task RunForResourceAsync(OpenLdapResource resource, CancellationToken ct)
    {
        var parser = new OpenLdapLogParser(resource.Name, _logger);
        var allEnv = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key is string k &&
                (k.Contains("OTEL", StringComparison.OrdinalIgnoreCase)
                 || k.Contains("DASHBOARD", StringComparison.OrdinalIgnoreCase)
                 || k.Contains("OTLP", StringComparison.OrdinalIgnoreCase)
                 || k.Contains("DCP", StringComparison.OrdinalIgnoreCase)
                 || k.Contains("ASPIRE", StringComparison.OrdinalIgnoreCase)))
            .Select(e => $"{e.Key}={e.Value}")
            .OrderBy(s => s);
        _logger.LogInformation(
            "Watching openldap '{Resource}'. HasListeners={HasListeners}. Env:\n  {Env}",
            resource.Name,
            OpenLdapDiagnostics.Source.HasListeners(),
            string.Join("\n  ", allEnv));

        try
        {
            await foreach (var batch in _loggers.WatchAsync(resource).WithCancellation(ct).ConfigureAwait(false))
            {
                foreach (var line in batch)
                {
                    try
                    {
                        parser.ProcessLine(line.Content);
                    }
                    catch (Exception ex)
                    {
                        // One malformed line should not take down the whole watcher.
                        _logger.LogDebug(ex, "Failed to parse slapd log line: {Line}", line.Content);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenLDAP log watcher for '{Resource}' terminated unexpectedly.", resource.Name);
        }
    }
}
