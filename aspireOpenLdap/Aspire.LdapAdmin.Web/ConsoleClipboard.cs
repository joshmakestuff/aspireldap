using Microsoft.JSInterop;

namespace Aspire.LdapAdmin.Web;

/// <summary>
/// The console's one copy-to-clipboard path (#119): both Browse and the search panel go
/// through here, so the guarantee lives in a single place — <see cref="CopyAsync"/> never
/// throws. The JS side already reports failure as <c>false</c> instead of rejecting; the
/// catches below are the authoritative circuit protection should the interop itself fault
/// (disconnect mid-call, module load failure). Scoped per circuit, like the toast service.
/// </summary>
public sealed class ConsoleClipboard(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    /// <summary>Copies <paramref name="text"/>; false means the caller should say so.</summary>
    public async ValueTask<bool> CopyAsync(string text)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/console.js").ConfigureAwait(false);
            return await _module.InvokeAsync<bool>("copyText", text).ConfigureAwait(false);
        }
        catch (JSException)
        {
            return false;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // Circuit gone; nothing to release.
            }
        }
    }
}
