using System.Net;
using System.Net.Sockets;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// A test-owned loopback TCP endpoint that accepts connections and never responds, so an LDAP
/// request is genuinely dispatched but no reply ever arrives. <see cref="FirstConnection"/>
/// signals that dispatch has begun; disposing the server resets every accepted connection,
/// which deterministically unblocks any client still waiting on a response. Replaces
/// environmental failure injectors (TEST-NET addresses) whose behavior depends on the
/// machine's network stack.
/// </summary>
internal sealed class LoopbackTcpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<TcpClient> _accepted = [];
    private readonly TaskCompletionSource _firstConnection =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LoopbackTcpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Completes when the first client connects (i.e. a request was dispatched).</summary>
    public Task FirstConnection => _firstConnection.Task;

    /// <summary>
    /// Returns a loopback port that is currently closed, by briefly binding an ephemeral port
    /// and releasing it. A connection attempt is refused in milliseconds — the deterministic
    /// fast-failure injector — without assuming any fixed well-known port is unbound. (The OS
    /// could in principle hand the port to another process before the test dials it, but
    /// ephemeral ports are not immediately reused; the race is negligible.)
    /// </summary>
    public static int ReserveClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Dispose();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                lock (_accepted)
                {
                    _accepted.Add(client);
                }
                _firstConnection.TrySetResult();
            }
        }
        catch (Exception)
        {
            // Listener disposed — test is done.
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        lock (_accepted)
        {
            foreach (var client in _accepted)
            {
                // LingerState zero → RST on close, so a peer blocked on a read fails NOW
                // rather than waiting for a graceful FIN exchange it may never finish.
                try
                {
                    client.LingerState = new LingerOption(enable: true, seconds: 0);
                }
                catch (Exception)
                {
                    // Socket may already be dead; disposal below still applies.
                }
                client.Dispose();
            }
            _accepted.Clear();
        }
    }
}
