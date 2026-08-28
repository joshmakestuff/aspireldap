using Microsoft.JSInterop;

namespace Aspire.LdapAdmin.Web;

/// <summary>
/// Client-side file save for text the component already holds: a Blob URL and a
/// synthetic anchor click, mirroring <see cref="ConsoleClipboard"/>'s guarantees —
/// <see cref="SaveAsync"/> never throws; false means the caller should say so. Scoped per
/// circuit, like the clipboard.
/// </summary>
public sealed class ConsoleDownload(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    /// <summary>Offers <paramref name="text"/> as a download named <paramref name="filename"/>.</summary>
    public async ValueTask<bool> SaveAsync(string filename, string text)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/console.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("downloadText", filename, text).ConfigureAwait(false);
            return true;
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
            }
        }
    }
}
