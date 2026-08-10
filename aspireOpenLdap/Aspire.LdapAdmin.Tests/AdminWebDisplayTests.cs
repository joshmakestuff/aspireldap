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

/// <summary>
/// The attribute value display cap (#98, absorbed #100): at most the AppHost-set cap of values
/// renders, and a cap in effect is always surfaced — "N of M values" plus an explicit expand —
/// never silent.
/// </summary>
public class AttributeValueDisplayCapTests
{
    [Fact]
    public void More_values_than_the_cap_render_exactly_the_cap_and_say_so()
    {
        var plan = EntryView.PlanValues(total: 349, cap: 20, expanded: false);

        Assert.Equal(20, plan.Shown);
        Assert.Equal(349, plan.Total);
        Assert.True(plan.Capped);
        // The count badge itself states the cap, pairing with the expand affordance.
        Assert.Equal("20 of 349 values", EntryView.CountBadge(plan));
    }

    [Fact]
    public void An_explicit_expand_shows_every_value()
    {
        var plan = EntryView.PlanValues(total: 349, cap: 20, expanded: true);

        Assert.Equal(349, plan.Shown);
        Assert.False(plan.Capped);
        Assert.Equal("349 values", EntryView.CountBadge(plan));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)] // exactly the cap: nothing is cut off, so nothing may claim to be
    public void At_or_below_the_cap_every_value_renders_uncapped(int total)
    {
        var plan = EntryView.PlanValues(total, cap: 20, expanded: false);

        Assert.Equal(total, plan.Shown);
        Assert.False(plan.Capped);
        Assert.Equal($"{total} values", EntryView.CountBadge(plan));
    }

    [Fact]
    public void The_cap_is_never_silent()
    {
        // Whenever fewer than every value renders, the plan says so — the invariant the
        // "always surfaced" doctrine rests on.
        for (var total = 0; total <= 45; total++)
        {
            var plan = EntryView.PlanValues(total, cap: 20, expanded: false);
            Assert.Equal(plan.Shown < plan.Total, plan.Capped);
        }
    }
}

/// <summary>
/// The web host's settings enums mirror the hosting library's options enums by name — the env
/// contract carries enum names, so a member renamed on either side without the other is a break
/// this test turns into words.
/// </summary>
public class LdapAdminSettingsContractTests
{
    [Fact]
    public void Theme_names_match_the_hosting_options_enum()
    {
        Assert.Equal(
            Enum.GetNames<Aspire.Hosting.ApplicationModel.LdapAdminTheme>(),
            Enum.GetNames<Web.LdapAdminTheme>());
    }

    [Fact]
    public void Sort_order_names_match_the_hosting_options_enum()
    {
        Assert.Equal(
            Enum.GetNames<Aspire.Hosting.ApplicationModel.LdapAdminSortOrder>(),
            Enum.GetNames<Web.LdapAdminSortOrder>());
    }

    [Fact]
    public void Web_defaults_equal_hosting_defaults()
    {
        // A host that receives no LdapAdmin__* configuration must behave exactly like one
        // handed the hosting defaults explicitly.
        var hosting = new Aspire.Hosting.ApplicationModel.LdapAdminOptions();
        var web = new Web.LdapAdminSettings();

        Assert.Equal(hosting.Theme.ToString(), web.Theme.ToString());
        Assert.Equal(hosting.DefaultSearchLimit, web.DefaultSearchLimit);
        Assert.Equal(hosting.DefaultSortOrder.ToString(), web.DefaultSortOrder.ToString());
        Assert.Equal(hosting.AttributeValueDisplayCap, web.AttributeValueDisplayCap);
    }
}
