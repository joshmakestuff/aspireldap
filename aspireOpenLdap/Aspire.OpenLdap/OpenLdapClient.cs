using System.Diagnostics;
using System.DirectoryServices.Protocols;

namespace Aspire.OpenLdap;

/// <summary>
/// An instrumented wrapper over an <see cref="LdapConnection"/>. Use <see cref="Send"/> /
/// <see cref="SendAsync"/> instead of calling the connection directly to emit OpenTelemetry
/// traces and metrics (source/meter <c>Aspire.OpenLdap</c>) for each LDAP operation.
/// </summary>
/// <remarks>
/// Resolve this type (transient) from DI after calling <c>AddOpenLdapClient</c>. It owns and
/// disposes the wrapped connection. Like <see cref="LdapConnection"/> it is NOT thread-safe;
/// because it is transient, each scope gets its own instance — do not share one across
/// concurrent operations.
/// </remarks>
public sealed class OpenLdapClient : IDisposable
{
    private readonly LdapConnection _connection;
    private readonly OpenLdapClientSettings _settings;
    private readonly string _serverAddress;
    private readonly int _serverPort;
    private bool _disposed;

    internal OpenLdapClient(
        LdapConnection connection,
        OpenLdapClientSettings settings,
        OpenLdapConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(connectionString);

        _connection = connection;
        _settings = settings;
        _serverAddress = connectionString.Endpoint.Host;
        _serverPort = connectionString.Endpoint.Port;
    }

    /// <summary>
    /// The underlying connection. Operations issued directly on it are NOT instrumented — prefer
    /// <see cref="Send"/> / <see cref="SendAsync"/>. The client owns this connection; do not
    /// dispose it yourself.
    /// </summary>
    public LdapConnection Connection => _connection;

    /// <summary>Sends a request and returns the response, emitting a span and a duration metric.</summary>
    public DirectoryResponse Send(DirectoryRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var op = OpenLdapInstrumentation.OperationName(request);
        var start = Stopwatch.GetTimestamp();
        var activity = StartActivity(op, request);

        ResultCode? code = null;
        Exception? failure = null;
        DirectoryResponse? response = null;
        try
        {
            response = _connection.SendRequest(request);
            code = response.ResultCode;
            return response;
        }
        catch (DirectoryOperationException ex)
        {
            code = ex.Response?.ResultCode;
            failure = ex;
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            Finish(activity, op, start, response, code, failure);
        }
    }

    /// <summary>
    /// Sends a request asynchronously, emitting a span and a duration metric. The
    /// <paramref name="cancellationToken"/> is observed before dispatch only; once the request is
    /// in flight it cannot be cancelled (a limitation of the underlying APM API).
    /// </summary>
    public async Task<DirectoryResponse> SendAsync(DirectoryRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var op = OpenLdapInstrumentation.OperationName(request);
        var start = Stopwatch.GetTimestamp();
        var activity = StartActivity(op, request);

        ResultCode? code = null;
        Exception? failure = null;
        DirectoryResponse? response = null;
        try
        {
            response = await Task.Factory.FromAsync(
                (callback, state) => _connection.BeginSendRequest(
                    request, PartialResultProcessing.NoPartialResultSupport, callback, state),
                _connection.EndSendRequest,
                state: null).ConfigureAwait(false);
            code = response.ResultCode;
            return response;
        }
        catch (DirectoryOperationException ex)
        {
            code = ex.Response?.ResultCode;
            failure = ex;
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            Finish(activity, op, start, response, code, failure);
        }
    }

    /// <summary>Disposes the wrapped connection. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }

    private Activity? StartActivity(string operation, DirectoryRequest request)
    {
        if (_settings.DisableTracing)
        {
            return null;
        }

        var activity = OpenLdapInstrumentation.ActivitySource.StartActivity($"LDAP {operation}", ActivityKind.Client);
        if (activity is not null)
        {
            OpenLdapInstrumentation.SetRequestTags(activity, request, operation, _serverAddress, _serverPort);
        }

        return activity;
    }

    private void Finish(Activity? activity, string operation, long startTimestamp, DirectoryResponse? response, ResultCode? code, Exception? failure)
    {
        var seconds = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

        if (activity is not null)
        {
            OpenLdapInstrumentation.SetResponseTags(activity, response, code, failure);
            activity.Dispose();
        }

        if (!_settings.DisableMetrics)
        {
            OpenLdapInstrumentation.OperationDuration.Record(
                seconds, OpenLdapInstrumentation.BuildMetricTags(operation, _serverAddress, _serverPort, code, failure));
        }
    }
}
