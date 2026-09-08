using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components;
using Bunit;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class DebouncedInputTests : TestContext
{
    [Fact]
    public async Task Debouncer_Runs_Only_The_Latest_Action()
    {
        using var debouncer = new Debouncer();
        var firstRan = false;
        var secondRan = false;

        var first = debouncer.DebounceAsync(TimeSpan.FromDays(1), () =>
        {
            firstRan = true;
            return Task.CompletedTask;
        });
        var second = debouncer.DebounceAsync(TimeSpan.Zero, () =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(firstRan);
        Assert.True(secondRan);
    }

    [Fact]
    public async Task Disposing_Debouncer_Cancels_The_Pending_Action()
    {
        var debouncer = new Debouncer();
        var ran = false;
        var pending = debouncer.DebounceAsync(TimeSpan.FromDays(1), () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        debouncer.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(ran);
    }

    [Fact]
    public async Task A_Disposed_Debouncer_Rejects_New_Work()
    {
        var debouncer = new Debouncer();
        debouncer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            debouncer.DebounceAsync(TimeSpan.Zero, () => Task.CompletedTask));
    }

    [Fact]
    public void Input_Keeps_Raw_Typing_Local_And_Publishes_Only_The_Latest_Value()
    {
        List<string> published = [];
        var cut = RenderComponent<DebouncedInput>(parameters => parameters
            .Add(p => p.Delay, TimeSpan.FromMilliseconds(50))
            .Add(p => p.ValueChanged, value => published.Add(value)));

        var input = cut.Find("input");
        input.Input("i");
        input.Input("inetOrgPerson");

        Assert.Empty(published);
        cut.WaitForAssertion(
            () => Assert.Equal(["inetOrgPerson"], published),
            TimeSpan.FromSeconds(2));
    }
}
