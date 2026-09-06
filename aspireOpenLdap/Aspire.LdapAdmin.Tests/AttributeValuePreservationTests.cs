using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web.Components.Directory;
using Bunit;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class AttributeValuePreservationTests : TestContext
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Saving_Unchanged_Preserves_Value_Boundaries_And_Contents(bool singleValued, bool binary)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        string[] original = binary
            ? ["AAEC", "", "/w=="]
            : singleValued
                ? ["first\nsecond\r\nthird\rfourth"]
                : ["first\nsecond", "", " leading and trailing \t", "CRLF\r\nCR\r", "\"quoted\"\\literal\\n", "\n"];
        IReadOnlyList<string>? saved = null;
        var model = new AttributeDialogModel
        {
            Name = singleValued ? "uidNumber" : "description",
            Entry = new LdapEntry("cn=test,dc=example", []),
            IsBinary = binary,
            Values = original,
            SaveAsync = (dialog, _) =>
            {
                saved = dialog.Values;
                return Task.FromResult<string?>(null);
            },
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Schema, singleValued ? ConsoleTestSchema.Schema : null));

        Assert.Equal(original.Length, cut.FindAll("textarea").Count);
        cut.Find("button.btn-primary").Click();

        Assert.NotNull(saved);
        Assert.Equal(original, saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editing_One_Field_Preserves_Others_And_Does_Not_Mutate_The_Source(bool singleValued)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        string[] original = singleValued ? ["old"] : ["old", "", "other\r\nvalue"];
        var model = new AttributeDialogModel
        {
            Name = singleValued ? "uidNumber" : "description",
            Entry = new LdapEntry("cn=test,dc=example", []),
            Values = original,
            SaveAsync = (_, _) => Task.FromResult<string?>("Write refused"),
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Schema, singleValued ? ConsoleTestSchema.Schema : null));

        cut.Find("textarea").Change(" new\nmultiline\nvalue ");
        cut.Find("button.btn-primary").Click();

        Assert.Equal("old", original[0]);
        Assert.Equal(" new\nmultiline\nvalue ", model.Values[0]);
        Assert.Equal(original.Skip(1), model.Values.Skip(1));
        Assert.Equal("Write refused", cut.Find(".bar.err").TextContent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Remove_And_Add_Distinguish_Deletion_From_Empty_Values(int count)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        IReadOnlyList<string>? saved = null;
        var model = new AttributeDialogModel
        {
            Entry = new LdapEntry("cn=test,dc=example", []),
            Values = ["old\nvalue", "other"],
            SaveAsync = (dialog, _) =>
            {
                saved = dialog.Values;
                return Task.FromResult<string?>(null);
            },
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters.Add(p => p.Model, model));

        cut.FindAll("button").First(b => b.TextContent == "Remove value").Click();
        Assert.Equal("other", Assert.Single(model.Values));
        cut.FindAll("button").Single(b => b.TextContent == "Remove value").Click();
        Assert.Empty(cut.FindAll("textarea"));
        for (var i = 0; i < count; i++)
        {
            cut.FindAll("button").Single(b => b.TextContent == "Add value").Click();
        }
        cut.Find("button.btn-primary").Click();

        Assert.NotNull(saved);
        Assert.Equal(count, saved.Count);
        Assert.All(saved, value => Assert.Equal(string.Empty, value));
    }

    [Fact]
    public void SingleValued_Can_Remove_And_ReAdd_And_Locks_Editing_During_Save()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var pending = new TaskCompletionSource<string?>();
        var model = new AttributeDialogModel
        {
            Name = "uidNumber",
            Entry = new LdapEntry("cn=test,dc=example", []),
            Values = ["first\nsecond"],
            SaveAsync = (_, _) => pending.Task,
        };
        var cut = RenderComponent<AttributeDialog>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Schema, ConsoleTestSchema.Schema));

        cut.FindAll("button").Single(b => b.TextContent == "Remove value").Click();
        var add = cut.FindAll("button").Single(b => b.TextContent == "Add value");
        Assert.False(add.HasAttribute("disabled"));
        add.Click();
        Assert.Equal(string.Empty, Assert.Single(model.Values));
        cut.Find("button.btn-primary").Click();

        Assert.True(cut.Find("textarea").HasAttribute("disabled"));
        Assert.All(cut.FindAll("button").Where(b => b.TextContent is "Add value" or "Remove value"),
            button => Assert.True(button.HasAttribute("disabled")));
        pending.SetResult(null);
    }
}
