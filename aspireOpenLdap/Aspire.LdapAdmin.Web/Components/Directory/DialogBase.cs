using Microsoft.AspNetCore.Components;

namespace Aspire.LdapAdmin.Web.Components.Directory;

/// <summary>
/// The dialogs' shared save protocol: one busy flag, one inline error, close only on
/// success. A returned error renders inline and the dialog stays up, so a failed write
/// never dismisses the user's input; Busy feeds ConsoleDialog's dismissal guard (#117).
/// </summary>
public abstract class DialogBase : ComponentBase
{
    /// <summary>Raised when the dialog is done (saved or cancelled); the shell removes it.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>The inline error — from pre-submit validation or the save delegate; null = none.</summary>
    protected string? Error { get; set; }

    /// <summary>True while the save delegate is in flight; disables every dismissal path.</summary>
    protected bool Busy { get; private set; }

    protected Task CloseAsync() => OnClose.InvokeAsync();

    /// <summary>Runs the save delegate under the protocol: null closes; a string renders inline.</summary>
    protected async Task SaveAsync(Func<Task<string?>> save)
    {
        Busy = true;
        Error = null;
        try
        {
            Error = await save();
        }
        finally
        {
            Busy = false;
        }

        if (Error is null)
        {
            await CloseAsync();
        }
    }
}
