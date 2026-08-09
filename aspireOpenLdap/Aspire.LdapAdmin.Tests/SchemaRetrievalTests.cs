using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Reading the live server's schema. The assertions are on <c>LdifDotNet.Schema</c> types
/// straight from the package — there is no admin-side schema model between the subschema
/// subentry and the consumer, so there is nothing here to pin but the retrieval itself.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class SchemaRetrievalTests(LdapAdminAppHostFixture fixture)
{
    /// <summary>Directory String — what <c>cn</c> resolves to through <c>SUP name</c>.</summary>
    private const string DirectoryStringOid = "1.3.6.1.4.1.1466.115.121.1.15";

    [Fact]
    public async Task The_live_schema_is_available_and_carries_definitions_of_all_three_kinds()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Schema.GetSchemaAsync(cts.Token);

        Assert.True(result.Available, result.UnavailableReason);
        Assert.Null(result.UnavailableReason);
        Assert.NotEmpty(result.Schema.AttributeTypes);
        Assert.NotEmpty(result.Schema.ObjectClasses);
        Assert.NotEmpty(result.Schema.Syntaxes);
    }

    [Fact]
    public async Task An_object_class_is_published_with_the_attributes_it_requires_and_allows()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Schema.GetSchemaAsync(cts.Token);

        var inetOrgPerson = result.Schema.FindObjectClass("inetOrgPerson");
        Assert.NotNull(inetOrgPerson);
        // Inherited through person -> top, which only the schema's own superior walk resolves.
        Assert.Contains("sn", result.Schema.RequiredAttributeNames(inetOrgPerson), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("cn", result.Schema.RequiredAttributeNames(inetOrgPerson), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("mail", result.Schema.OptionalAttributeNames(inetOrgPerson), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_attribute_type_with_no_syntax_of_its_own_resolves_one_through_its_superior()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Schema.GetSchemaAsync(cts.Token);

        var cn = result.Schema.FindAttributeType("cn");
        Assert.NotNull(cn);
        Assert.Null(cn.Syntax);
        Assert.Equal(DirectoryStringOid, result.Schema.ResolveSyntaxOid(cn));
    }

    [Fact]
    public async Task The_server_publishes_syntaxes_it_declares_to_be_non_human_readable()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Schema.GetSchemaAsync(cts.Token);

        Assert.Contains(result.Schema.Syntaxes, s => s.NotHumanReadable || s.BinaryTransferRequired);
    }

    [Fact]
    public async Task An_attribute_type_can_be_found_by_its_oid_as_well_as_by_name()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Schema.GetSchemaAsync(cts.Token);

        var byName = result.Schema.FindAttributeType("userPassword");
        Assert.NotNull(byName);
        Assert.Same(byName, result.Schema.FindAttributeType(byName.Oid));
    }

    [Fact]
    public async Task A_schema_that_was_read_once_is_not_read_again()
    {
        using var cts = TestCancellation.Source();

        var first = await fixture.Schema.GetSchemaAsync(cts.Token);
        var second = await fixture.Schema.GetSchemaAsync(cts.Token);

        Assert.Same(first, second);
    }
}
