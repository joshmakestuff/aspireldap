using System.ComponentModel;
using System.Diagnostics;

namespace Aspire.Hosting.OpenLdap;

/// <summary>
/// Runs container-runtime CLI commands (docker/podman) for the dashboard commands.
/// Abstracted behind an interface so command handlers are testable without depending on a
/// developer's global container-CLI state (#58).
/// </summary>
internal interface IContainerCliRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and waits for exit.
    /// A missing/unlaunchable executable never throws — it surfaces as
    /// <see cref="ProcessContainerCliRunner.StartFailureExitCode"/> with the reason in StdErr,
    /// so handlers return an actionable dashboard message instead of an unhandled exception.
    /// Cancellation kills the whole child process tree before the cancellation propagates.
    /// </summary>
    Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed class ProcessContainerCliRunner : IContainerCliRunner
{
    /// <summary>
    /// Sentinel for "the CLI never started" (executable missing, spawn failure). int.MinValue
    /// cannot collide with a real exit code we would otherwise misreport.
    /// </summary>
    internal const int StartFailureExitCode = int.MinValue;

    /// <summary>Test hook: observes the child PID so kill-on-cancel is provable.</summary>
    internal Action<int>? OnProcessStarted { get; set; }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return (StartFailureExitCode, string.Empty,
                $"Container runtime '{fileName}' could not be started: {ex.Message}. Install it, " +
                "or point Aspire at the runtime you use (e.g. ASPIRE_CONTAINER_RUNTIME=podman).");
        }
        if (proc is null)
        {
            return (StartFailureExitCode, string.Empty,
                $"Container runtime '{fileName}' could not be started.");
        }

        using (proc)
        {
            OnProcessStarted?.Invoke(proc.Id);
            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return (proc.ExitCode,
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // The dashboard reports the command as cancelled the moment this throws; the
                // child must not keep mutating state (e.g. deleting a volume) past that point.
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(milliseconds: 5000);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between WaitForExitAsync observing cancellation and Kill.
                }
                catch (Win32Exception)
                {
                    // Exiting concurrently with the kill; nothing left to terminate.
                }
                throw;
            }
        }
    }
}
