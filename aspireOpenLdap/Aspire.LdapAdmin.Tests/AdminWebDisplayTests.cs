using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components.Directory;
using Aspire.LdapAdmin.Web.Components.Pages;
using System.DirectoryServices.Protocols;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The DN display contract from issue #83 (old #55): the suffix every result row shares is
/// stated once, rows show the relative form, and stripping never introduces ambiguity —
/// escaped commas are not split points, and a row whose whole DN is the suffix keeps it.
/// </summary>
public class DnDisplayTests
{
    [Fact]
    public void The_shared_suffix_is_the_components_every_dn_repeats()
    {
        var suffix = DnDisplay.CommonSuffix(
        [
            "uid=alice,ou=people,dc=example,dc=org",
            "uid=bob,ou=people,dc=example,dc=org",
        ]);

        Assert.Equal("ou=people,dc=example,dc=org", suffix);
    }

    [Fact]
    public void A_single_dn_has_no_suffix_because_nothing_repeats()
    {
        Assert.Equal(string.Empty, DnDisplay.CommonSuffix(["uid=alice,dc=example,dc=org"]));
    }

    [Fact]
    public void Dns_with_no_shared_tail_yield_no_suffix()
    {
        Assert.Equal(string.Empty, DnDisplay.CommonSuffix(["dc=a", "dc=b"]));
    }

    [Fact]
    public void An_escaped_comma_inside_a_value_is_not_a_split_point()
    {
        // RFC 4514 allows \, inside a value; a naive split would strip mid-component and
        // hand back a "relative DN" that names a different entry.
        var suffix = DnDisplay.CommonSuffix(
        [
            @"cn=Doe\, Jane,ou=people,dc=example,dc=org",
            @"cn=Doe\, John,ou=people,dc=example,dc=org",
        ]);

        Assert.Equal("ou=people,dc=example,dc=org", suffix);
        Assert.Equal(@"cn=Doe\, Jane", DnDisplay.Relative(@"cn=Doe\, Jane,ou=people,dc=example,dc=org", suffix));
    }

    [Fact]
    public void A_row_whose_whole_dn_is_the_suffix_keeps_the_full_dn()
    {
        // The search base itself can appear in its own results; showing it as an empty
        // string would name nothing.
        var dns = new[]
        {
            "ou=people,dc=example,dc=org",
            "uid=alice,ou=people,dc=example,dc=org",
        };
        var suffix = DnDisplay.CommonSuffix(dns);

        Assert.Equal("ou=people,dc=example,dc=org", DnDisplay.Relative(dns[0], suffix));
        Assert.Equal("uid=alice", DnDisplay.Relative(dns[1], suffix));
    }

    [Fact]
    public void Relative_strips_only_a_strict_suffix()
    {
        Assert.Equal(
            "uid=alice,ou=other,dc=example,dc=com",
            DnDisplay.Relative("uid=alice,ou=other,dc=example,dc=com", "ou=people,dc=example,dc=org"));
    }
}

/// <summary>
/// Write outcomes render as UI states, not crashes: every <see cref="LdapOperationStatus"/>
/// the service can report maps to words, and the server's own diagnostic is kept when it
/// sent one.
/// </summary>
public class OperationOutcomeDisplayTests
{
    [Theory]
    [InlineData(LdapOperationStatus.NotFound)]
    [InlineData(LdapOperationStatus.AlreadyExists)]
    [InlineData(LdapOperationStatus.AccessDenied)]
    [InlineData(LdapOperationStatus.NotAllowedOnNonLeaf)]
    [InlineData(LdapOperationStatus.SchemaViolation)]
    [InlineData(LdapOperationStatus.ConstraintViolation)]
    [InlineData(LdapOperationStatus.InvalidRequest)]
    [InlineData(LdapOperationStatus.Refused)]
    [InlineData(LdapOperationStatus.Failed)]
    public void Every_failure_status_has_words(LdapOperationStatus status)
    {
        var text = Browse.Describe(new LdapOperationResult(status));

        Assert.False(string.IsNullOrWhiteSpace(text));
        // The raw enum name is an implementation detail, not a sentence.
        Assert.NotEqual(status.ToString(), text);
    }

    [Fact]
    public void The_servers_own_diagnostic_is_kept()
    {
        var text = Browse.Describe(new LdapOperationResult(
            LdapOperationStatus.AccessDenied,
            ResultCode.InsufficientAccessRights,
            "no write access to ou=people"));

        Assert.Contains("Access denied", text, StringComparison.Ordinal);
        Assert.Contains("no write access to ou=people", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmodelled_result_code_is_still_named_rather_than_flattened()
    {
        var text = Browse.Describe(new LdapOperationResult(LdapOperationStatus.Failed, ResultCode.Busy));

        Assert.Contains(nameof(ResultCode.Busy), text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Binary values are labelled by decoded size without being decoded; the label must not lie
/// about the byte count.
/// </summary>
public class BinaryValueLabelTests
{
    [Theory]
    [InlineData("", "0 bytes")]        // empty value
    [InlineData("AA==", "1 byte")]     // 1 byte
    [InlineData("AAA=", "2 bytes")]    // 2 bytes
    [InlineData("AAAA", "3 bytes")]    // 3 bytes, no padding
    public void The_size_label_matches_the_decoded_length(string base64, string expected)
    {
        Assert.Equal(expected, EntryView.DescribeBinary(base64));
    }
}
