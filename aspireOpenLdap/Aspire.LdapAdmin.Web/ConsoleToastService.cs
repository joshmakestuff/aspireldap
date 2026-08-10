namespace Aspire.LdapAdmin.Web;

/// <summary>
/// The console's toast slot (design handoff § Toast): one message at a time, bottom center,
/// announced via aria-live. Panels raise messages here; the shell page subscribes and
/// renders. Scoped per circuit — a toast belongs to the user who caused it.
/// </summary>
public sealed class ConsoleToastService
{
    /// <summary>Raised with the message to show; the shell renders and auto-clears it.</summary>
    public event EventHandler<ToastEventArgs>? Shown;

    public void Show(string message) => Shown?.Invoke(this, new ToastEventArgs(message));
}

public sealed class ToastEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
