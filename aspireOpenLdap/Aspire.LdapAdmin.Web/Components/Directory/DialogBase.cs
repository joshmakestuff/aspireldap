using Microsoft.AspNetCore.Components;

namespace Aspire.LdapAdmin.Web.Components.Directory;

/// <summary>
/// The dialogs' shared save protocol: one cancellation source per save, one busy flag,
/// one inline error, and close only on success. Cancelling an in-flight save requests
/// cancellation but leaves the dialog mounted until the delegate acknowledges it.
/// </summary>
public abstract class DialogBase : ComponentBase, IDisposable
{
    private CancellationTokenSource? _saveCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    /// <summary>Raised when the dialog is done (saved or cancelled); the shell removes it.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>The inline error — from pre-submit validation or the save delegate; null = none.</summary>
    protected string? Error { get; set; }

    /// <summary>True while the save delegate is in flight; locks the dialog controls.</summary>
    protected bool Busy { get; private set; }

    /// <summary>True after cancellation was requested and before the save acknowledges it.</summary>
    protected bool CancellationRequested { get; private set; }

    /// <summary>Cancels when the dialog is removed, for non-save work such as lookups.</summary>
    protected CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    protected Task CancelAsync()
    {
        if (!Busy)
        {
            return OnClose.InvokeAsync();
        }

        if (_saveCancellation is { IsCancellationRequested: false } cancellation)
        {
            CancellationRequested = true;
            cancellation.Cancel();
        }
        return Task.CompletedTask;
    }

    /// <summary>Runs the save delegate under the protocol: null closes; a string renders inline.</summary>
    protected async Task SaveAsync(Func<CancellationToken, Task<string?>> save)
    {
        if (Busy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        Busy = true;
        CancellationRequested = false;
        Error = null;
        try
        {
            Error = await save(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Error = "The operation was cancelled.";
        }
        finally
        {
            _saveCancellation = null;
            Busy = false;
            CancellationRequested = false;
        }

        if (Error is null)
        {
            await OnClose.InvokeAsync();
        }
    }

    public void Dispose()
    {
        _lifetimeCancellation.Cancel();
        _saveCancellation?.Cancel();
        _lifetimeCancellation.Dispose();
    }
}
