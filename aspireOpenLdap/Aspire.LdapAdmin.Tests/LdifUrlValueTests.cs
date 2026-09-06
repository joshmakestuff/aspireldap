using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class LdifUrlValueTests
{
    private const string Dn = "cn=url,dc=example,dc=org";
    private const string Url = "file:///nonexistent-ldif-url-test";

    public static TheoryData<string, bool> UrlRecords
    {
        get
        {
            TheoryData<string, bool> cases = new();
            foreach (var kind in new[] { "content", "add", "modify-add", "modify-replace", "modify-delete" })
            {
                cases.Add(kind, false);
                cases.Add(kind, true);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(UrlRecords))]
    public async Task Url_values_reject_the_whole_plan_and_direct_apply(string kind, bool mixedBinary)
    {
        var values = (mixedBinary ? "description:: AAEC\n" : "description: ordinary\n")
            + $"description:< {Url}\n";
        var body = kind switch
        {
            "content" => values,
            "add" => "changetype: add\n" + values,
            _ => $"changetype: modify\n{kind[7..]}: description\n{values}-\n",
        };
        var ldif = "dn: ou=first,dc=example,dc=org\nobjectClass: organizationalUnit\nou: first\n\n"
            + $"dn: {Dn}\n{body}";
        var records = LdifReader.Parse(ldif);
        Assert.Equal(2, records.Count);

        var plan = LdapLdifService.ParsePlan(ldif);
        Assert.Empty(plan.Items);
        Assert.Contains("URL", plan.Error, StringComparison.Ordinal);
        Assert.Contains("description", plan.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Url, plan.Error, StringComparison.Ordinal);

        // Bypass ParsePlan. Any dispatch, including the supported first record, would
        // dereference this null directory and fail instead of passing the preflight assertion.
        var items = records.Select(static record => new LdifImportItem(record, "ignored", record.Dn, 0)).ToArray();
        var result = await new LdapLdifService(null!).ApplyAsync(items);
        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Applied);
        Assert.Equal(2, result.Total);
        Assert.Equal(Dn, result.FailedDn);
        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Outcome.Status);
        Assert.Equal(plan.Error, result.Outcome.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Diff_refuses_urls_before_comparison_or_text_and_binary_conversion(bool existing, bool binary)
    {
        var entry = new LdapEntry(Dn, existing
            ? [new LdapAttributeValues("description", binary, [binary ? "AAEC" : "old"], LdapValueClassification.Schema)]
            : []);
        var draft = $"dn: {Dn}\ndescription:< {Url}\n";

        var diff = LdapEntryLdif.Diff(entry, draft);
        Assert.Null(diff.Changes);
        Assert.Contains("URL", diff.Error, StringComparison.Ordinal);
        Assert.Equal(LdapLdifService.ParsePlan(draft).Error, diff.Error);
    }

    [Fact]
    public void Inline_url_text_and_base64_values_are_not_url_references()
    {
        var entry = new LdapEntry(Dn,
        [
            new("description", false, [Url, "", " leading whitespace "], LdapValueClassification.Schema),
            new("jpegPhoto", true, ["AAEC/w=="], LdapValueClassification.Schema),
        ]);
        var draft = LdapEntryLdif.Write(entry);
        Assert.Null(LdapLdifService.ParsePlan(draft).Error);
        var diff = LdapEntryLdif.Diff(entry, draft);
        Assert.Null(diff.Error);
        Assert.Empty(diff.Changes!);
    }
}

[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LdifUrlValueLiveTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task A_later_url_record_prevents_earlier_writes_but_inline_values_still_import()
    {
        using var cts = TestCancellation.Source();
        var firstDn = fixture.DnUnder("ou=url-preflight-first");
        var urlDn = fixture.DnUnder("ou=url-preflight-later");
        var first = $"dn: {firstDn}\nobjectClass: organizationalUnit\nou: url-preflight-first\n";
        var input = first + $"\ndn: {urlDn}\nobjectClass: organizationalUnit\nou: url-preflight-later\ndescription:< file:///nonexistent-ldif-url-test\n";
        var items = LdifReader.Parse(input).Select(static record => new LdifImportItem(record, "ignored", record.Dn, 0)).ToArray();
        var service = new LdapLdifService(fixture.Directory);
        try
        {
            var result = await service.ApplyAsync(items, cts.Token);
            Assert.Equal(LdapOperationStatus.InvalidRequest, result.Outcome.Status);
            Assert.Equal(0, result.Applied);
            Assert.Equal(urlDn, result.FailedDn);
            Assert.Null(await fixture.Directory.GetEntryAsync(firstDn, cancellationToken: cts.Token));
            Assert.Null(await fixture.Directory.GetEntryAsync(urlDn, cancellationToken: cts.Token));

            var inlinePlan = LdapLdifService.ParsePlan(first + "description: https://example.invalid/value\ndescription:: aW5saW5l\n");
            Assert.Null(inlinePlan.Error);
            var applied = await service.ApplyAsync(inlinePlan.Items, cts.Token);
            Assert.True(applied.Succeeded, applied.Outcome.Message);
            var entry = await fixture.Directory.GetEntryAsync(firstDn, ["description"], cts.Token);
            var values = Assert.Single(entry!.Attributes).Values;
            Assert.Equal(2, values.Count);
            Assert.Contains("https://example.invalid/value", values);
            Assert.Contains("inline", values);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(firstDn, cts.Token);
            await fixture.Directory.DeleteEntryAsync(urlDn, cts.Token);
        }
    }
}
