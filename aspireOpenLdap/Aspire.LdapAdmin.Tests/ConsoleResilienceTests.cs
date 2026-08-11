using Aspire.LdapAdmin.Web;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Xunit;
using MainLayout = Aspire.LdapAdmin.Web.Components.Layout.MainLayout;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The #119 contract: no clipboard failure — missing API, rejection, dead circuit — may
/// ever escape into a Blazor event handler. The guarantee lives in ConsoleClipboard, the
/// single copy path both Browse and the search panel use.
/// </summary>
public sealed class ConsoleClipboardTests : TestContext
{
    [Fact]
    public async Task CopyAsync_Returns_False_Instead_Of_Throwing_When_The_Interop_Rejects()
    {
        // The deterministic non-secure-origin shape: the dotted lookup / write rejects.
        var module = JSInterop.SetupModule("./js/console.js");
        module.Setup<bool>("copyText", "uid=alice,dc=example,dc=org")
            .SetException(new JSException("Could not find 'clipboard' in 'navigator'."));
        var clipboard = new ConsoleClipboard(JSInterop.JSRuntime);

        var ok = await clipboard.CopyAsync("uid=alice,dc=example,dc=org");

        Assert.False(ok);
    }

    [Fact]
    public async Task CopyAsync_Reports_The_Outcome_The_Copy_Script_Returns()
    {
        var module = JSInterop.SetupModule("./js/console.js");
        module.Setup<bool>("copyText", "uid=alice,dc=example,dc=org").SetResult(true);
        var clipboard = new ConsoleClipboard(JSInterop.JSRuntime);

        Assert.True(await clipboard.CopyAsync("uid=alice,dc=example,dc=org"));
    }
}

/// <summary>
/// The layout's ErrorBoundary (#119): an exception escaping any handler inside the shell
/// degrades to an inline error with a way back, never a dead circuit.
/// </summary>
public sealed class MainLayoutErrorBoundaryTests : TestContext
{
    /// <summary>A component whose click handler throws — the #119 failure shape, owned by
    /// a real component so the renderer routes the exception to the nearest boundary.</summary>
    private sealed class Boom : ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "id", "boom");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, () => throw new InvalidOperationException("boom")));
            builder.AddContent(3, "boom");
            builder.CloseElement();
        }
    }

    [Fact]
    public void A_Throwing_Handler_Renders_The_Error_Bar_And_Recover_Restores_The_Body()
    {
        RenderFragment body = builder =>
        {
            builder.OpenComponent<Boom>(0);
            builder.CloseComponent();
        };
        var cut = RenderComponent<MainLayout>(parameters => parameters
            .Add(p => p.Body, body));

        cut.Find("#boom").Click();

        // Contained: the error bar renders instead of the exception escaping the renderer.
        Assert.Contains("still alive", cut.Find(".bar.err").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#boom"));

        cut.Find(".bar.err button").Click();

        // Recovered: the body is back and the bar is gone.
        Assert.NotNull(cut.Find("#boom"));
        Assert.Empty(cut.FindAll(".bar.err"));
    }
}
