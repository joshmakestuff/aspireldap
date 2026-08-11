using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Writing the directory: the round trip, and every refusal a person editing entries actually
/// meets. Each refusal is asserted as an outcome the service returns, not as an exception —
/// "that already exists" is an answer to render, not a crash.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public class EntryWriteTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task An_entry_can_be_added_read_modified_and_deleted()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("uid", "roundtrip"), "ou=people");

        var added = await fixture.Directory.AddEntryAsync(Person(dn, "roundtrip", "Round Trip"), cts.Token);
        Assert.True(added.Succeeded, added.Message);

        try
        {
            var created = await fixture.Directory.GetEntryAsync(dn, cancellationToken: cts.Token);
            Assert.NotNull(created);
            Assert.Equal("Round Trip", Value(created, "cn"));

            var modified = await fixture.Directory.ModifyEntryAsync(
                dn,
                [
                    new LdapAttributeChange(DirectoryAttributeOperation.Replace, "cn", ["Renamed Value"]),
                    new LdapAttributeChange(DirectoryAttributeOperation.Add, "mail", ["roundtrip@example.org"]),
                ],
                cts.Token);
            Assert.True(modified.Succeeded, modified.Message);

            var after = await fixture.Directory.GetEntryAsync(dn, cancellationToken: cts.Token);
            Assert.NotNull(after);
            Assert.Equal("Renamed Value", Value(after, "cn"));
            Assert.Equal("roundtrip@example.org", Value(after, "mail"));
        }
        finally
        {
            var deleted = await fixture.Directory.DeleteEntryAsync(dn, cts.Token);
            Assert.True(deleted.Succeeded, deleted.Message);
        }

        Assert.Null(await fixture.Directory.GetEntryAsync(dn, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Deleting_an_attribute_removes_it_from_the_entry()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("uid", "attr-delete"), "ou=people");

        var added = await fixture.Directory.AddEntryAsync(
            Person(dn, "attr-delete", "Attribute Delete", extraMail: "attr-delete@example.org"), cts.Token);
        Assert.True(added.Succeeded, added.Message);

        try
        {
            var modified = await fixture.Directory.ModifyEntryAsync(
                dn, [new LdapAttributeChange(DirectoryAttributeOperation.Delete, "mail", [])], cts.Token);
            Assert.True(modified.Succeeded, modified.Message);

            var after = await fixture.Directory.GetEntryAsync(dn, cancellationToken: cts.Token);
            Assert.NotNull(after);
            Assert.DoesNotContain(after.Attributes, a => string.Equals(a.Name, "mail", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(dn, cts.Token);
        }
    }

    [Fact]
    public async Task A_binary_value_round_trips_as_base64_and_reads_back_classified_by_schema()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("uid", "binary"), "ou=people");

        // Bytes that are deliberately not valid UTF-8 in one value and plausibly text in
        // another: a projection that decided per value rather than per attribute would hand
        // back one string and one base64 blob for the same attribute.
        byte[][] photos = [[0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10], "not really a jpeg"u8.ToArray()];
        var person = Person(dn, "binary", "Binary Person");
        var entry = person with
        {
            Attributes =
            [
                .. person.Attributes,
                new LdapNewAttribute("jpegPhoto", [.. photos.Select(Convert.ToBase64String)], IsBase64: true),
            ],
        };

        var added = await fixture.Directory.AddEntryAsync(entry, cts.Token);
        Assert.True(added.Succeeded, added.Message);

        try
        {
            var read = await fixture.Directory.GetEntryAsync(dn, ["jpegPhoto"], cts.Token);
            Assert.NotNull(read);
            var photo = Assert.Single(read.Attributes);
            Assert.True(photo.IsBinary);
            Assert.Equal(LdapValueClassification.Schema, photo.Classification);
            Assert.Equal(
                photos.Select(Convert.ToBase64String).Order(StringComparer.Ordinal),
                photo.Values.Order(StringComparer.Ordinal));
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(dn, cts.Token);
        }
    }

    [Fact]
    public async Task Adding_an_entry_that_already_exists_reports_already_exists()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder("uid=alice", "ou=people");

        var result = await fixture.Directory.AddEntryAsync(Person(dn, "alice", "Alice Anderson"), cts.Token);

        Assert.Equal(LdapOperationStatus.AlreadyExists, result.Status);
        Assert.Equal(ResultCode.EntryAlreadyExists, result.ResultCode);
    }

    [Fact]
    public async Task Deleting_an_entry_that_does_not_exist_reports_not_found()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.DeleteEntryAsync(fixture.DnUnder("uid=absent", "ou=people"), cts.Token);

        Assert.Equal(LdapOperationStatus.NotFound, result.Status);
        Assert.Equal(ResultCode.NoSuchObject, result.ResultCode);
    }

    [Fact]
    public async Task Deleting_a_node_that_has_children_reports_not_allowed_on_non_leaf()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.DeleteEntryAsync(fixture.DnUnder("ou=people"), cts.Token);

        Assert.Equal(LdapOperationStatus.NotAllowedOnNonLeaf, result.Status);
    }

    [Fact]
    public async Task Modifying_an_entry_that_does_not_exist_reports_not_found()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.ModifyEntryAsync(
            fixture.DnUnder("uid=absent", "ou=people"),
            [new LdapAttributeChange(DirectoryAttributeOperation.Replace, "cn", ["x"])],
            cts.Token);

        Assert.Equal(LdapOperationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task An_entry_missing_an_attribute_its_object_class_requires_reports_a_schema_violation()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("uid", "no-sn"), "ou=people");

        var result = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry(dn, [
                new LdapNewAttribute("objectClass", ["inetOrgPerson"]),
                new LdapNewAttribute("uid", ["no-sn"]),
                new LdapNewAttribute("cn", ["No Surname"]),
            ]),
            cts.Token);

        Assert.Equal(LdapOperationStatus.SchemaViolation, result.Status);
        Assert.Equal(ResultCode.ObjectClassViolation, result.ResultCode);
    }

    [Fact]
    public async Task An_attribute_the_schema_does_not_define_reports_a_schema_violation()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("uid", "unknown-attr"), "ou=people");

        var entry = Person(dn, "unknown-attr", "Unknown Attribute");
        var result = await fixture.Directory.AddEntryAsync(
            entry with { Attributes = [.. entry.Attributes, new LdapNewAttribute("notAnAttribute", ["x"])] },
            cts.Token);

        Assert.Equal(LdapOperationStatus.SchemaViolation, result.Status);
        Assert.Equal(ResultCode.UndefinedAttributeType, result.ResultCode);
    }

    [Fact]
    public async Task A_dn_that_is_not_a_valid_dn_is_rejected_without_a_round_trip()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry("not a dn", [new LdapNewAttribute("objectClass", ["organizationalRole"])]), cts.Token);

        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Status);
        Assert.Null(result.ResultCode);
    }

    [Fact]
    public async Task A_value_that_is_not_valid_base64_is_rejected_without_a_round_trip()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("uid", "bad-base64"), "ou=people");

        var entry = Person(dn, "bad-base64", "Bad Base64");
        var result = await fixture.Directory.AddEntryAsync(
            entry with
            {
                Attributes = [.. entry.Attributes, new LdapNewAttribute("jpegPhoto", ["not base64!"], IsBase64: true)],
            },
            cts.Token);

        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Status);
        Assert.Null(result.ResultCode);
        Assert.Null(await fixture.Directory.GetEntryAsync(dn, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task An_attribute_name_that_is_not_a_valid_attribute_description_is_rejected_without_a_round_trip()
    {
        using var cts = TestCancellation.Source();
        var dn = fixture.DnUnder(Dn.Rdn("uid", "bad-attr-name"), "ou=people");

        var result = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry(dn, [new LdapNewAttribute("1nvalid name", ["x"])]), cts.Token);

        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Status);
        Assert.Null(result.ResultCode);
    }

    [Fact]
    public async Task An_entry_with_no_attributes_is_rejected_without_a_round_trip()
    {
        using var cts = TestCancellation.Source();

        var result = await fixture.Directory.AddEntryAsync(
            new LdapNewEntry(fixture.DnUnder(Dn.Rdn("uid", "empty"), "ou=people"), []), cts.Token);

        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Status);
    }

    [Fact]
    public async Task Subtree_delete_removes_the_branch_where_a_plain_delete_refuses()
    {
        using var cts = TestCancellation.Source();
        var parent = fixture.DnUnder(Dn.Rdn("ou", "subtree-del"), "ou=people");
        var inner = Dn.Combine(Dn.Rdn("ou", "inner"), parent);
        var leaf = Dn.Combine(Dn.Rdn("uid", "subtree-leaf"), inner);

        foreach (var (dn, name) in ((string Dn, string Name)[])[(parent, "subtree-del"), (inner, "inner")])
        {
            var addedOu = await fixture.Directory.AddEntryAsync(
                new LdapNewEntry(dn, [new("objectClass", ["organizationalUnit"]), new("ou", [name])]),
                cts.Token);
            Assert.True(addedOu.Succeeded, addedOu.Message);
        }
        var addedLeaf = await fixture.Directory.AddEntryAsync(Person(leaf, "subtree-leaf", "Subtree Leaf"), cts.Token);
        Assert.True(addedLeaf.Succeeded, addedLeaf.Message);

        // A plain delete of the non-leaf is the server's refusal, unchanged.
        var refused = await fixture.Directory.DeleteEntryAsync(parent, cts.Token);
        Assert.Equal(LdapOperationStatus.NotAllowedOnNonLeaf, refused.Status);

        // The subtree delete recurses children-first (the server has no Tree Delete
        // control — workspace findings.md 2026-08-10) and takes the whole branch.
        var deleted = await fixture.Directory.DeleteEntryAsync(parent, subtree: true, cts.Token);
        Assert.True(deleted.Succeeded, deleted.Message);

        Assert.Null(await fixture.Directory.GetEntryAsync(leaf, cancellationToken: cts.Token));
        Assert.Null(await fixture.Directory.GetEntryAsync(inner, cancellationToken: cts.Token));
        Assert.Null(await fixture.Directory.GetEntryAsync(parent, cancellationToken: cts.Token));
    }

    private static LdapNewEntry Person(string dn, string uid, string cn, string? extraMail = null)
    {
        List<LdapNewAttribute> attributes =
        [
            new("objectClass", ["inetOrgPerson"]),
            new("uid", [uid]),
            new("cn", [cn]),
            new("sn", [cn]),
        ];
        if (extraMail is not null)
        {
            attributes.Add(new LdapNewAttribute("mail", [extraMail]));
        }

        return new LdapNewEntry(dn, attributes);
    }

    private static string Value(LdapEntry entry, string attribute) =>
        Assert.Single(entry.Attributes, a => string.Equals(a.Name, attribute, StringComparison.OrdinalIgnoreCase)).Values[0];
}
