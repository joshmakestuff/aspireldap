using System.Text;
using Aspire.LdapAdmin.Core;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Reading one entry, and the decision that matters most while doing it: whether each
/// attribute's values are text or octets, and on what basis. A value reported as text that is
/// not text is a corrupted value, so the basis is asserted alongside the flag.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class EntryReadTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task An_entry_reads_back_with_the_attributes_it_was_seeded_with()
    {
        using var cts = TestCancellation.Source();

        var entry = await fixture.Directory.GetEntryAsync(fixture.DnUnder("uid=alice", "ou=people"), cancellationToken: cts.Token);

        Assert.NotNull(entry);
        Assert.Equal("Alice Anderson", Single(entry, "cn"));
        Assert.Equal("Anderson", Single(entry, "sn"));
        Assert.Equal("alice@example.org", Single(entry, "mail"));
        Assert.Equal("alice", Single(entry, "uid"));
    }

    [Fact]
    public async Task An_entry_that_does_not_exist_reads_as_null()
    {
        using var cts = TestCancellation.Source();

        var entry = await fixture.Directory.GetEntryAsync(
            fixture.DnUnder("uid=absent", "ou=people"), cancellationToken: cts.Token);

        Assert.Null(entry);
    }

    [Fact]
    public async Task An_octet_syntax_attribute_is_base64_because_the_schema_says_so()
    {
        using var cts = TestCancellation.Source();

        // userPassword's syntax is Octet String, which slapd flags neither X-NOT-HUMAN-READABLE
        // nor X-BINARY-TRANSFER-REQUIRED — so only the RFC-derived floor classifies it, and its
        // {SSHA} value is valid UTF-8, so byte inspection alone would have called it text.
        var entry = await fixture.Directory.GetEntryAsync(
            fixture.DnUnder("uid=alice", "ou=people"), ["userPassword"], cts.Token);

        Assert.NotNull(entry);
        var password = Assert.Single(entry.Attributes);
        Assert.True(password.IsBinary);
        Assert.Equal(LdapValueClassification.Schema, password.Classification);
        Assert.StartsWith("{SSHA}", Encoding.UTF8.GetString(Convert.FromBase64String(password.Values[0])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_string_syntax_attribute_is_text_because_the_schema_says_so()
    {
        using var cts = TestCancellation.Source();

        // cn is published as "SUP name" with no SYNTAX of its own: only walking the superior
        // chain resolves it to Directory String, and an unresolved attribute would fall through
        // to byte inspection.
        var entry = await fixture.Directory.GetEntryAsync(
            fixture.DnUnder("uid=alice", "ou=people"), ["cn"], cts.Token);

        Assert.NotNull(entry);
        var cn = Assert.Single(entry.Attributes);
        Assert.False(cn.IsBinary);
        Assert.Equal(LdapValueClassification.Schema, cn.Classification);
    }

    [Fact]
    public async Task A_requested_attribute_list_narrows_the_entry_projection()
    {
        using var cts = TestCancellation.Source();

        var entry = await fixture.Directory.GetEntryAsync(
            fixture.DnUnder("uid=alice", "ou=people"), ["cn", "mail"], cts.Token);

        Assert.NotNull(entry);
        Assert.Equal(["cn", "mail"], entry.Attributes.Select(a => a.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_dn_that_is_not_a_valid_dn_is_rejected_before_the_server_sees_it()
    {
        using var cts = TestCancellation.Source();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Directory.GetEntryAsync("not a dn", cancellationToken: cts.Token));
    }

    private static string Single(LdapEntry entry, string attribute) =>
        Assert.Single(entry.Attributes, a => string.Equals(a.Name, attribute, StringComparison.OrdinalIgnoreCase)).Values[0];
}
