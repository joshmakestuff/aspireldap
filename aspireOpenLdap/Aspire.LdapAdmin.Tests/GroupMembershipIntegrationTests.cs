using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GroupMembershipIntegrationTests(LdapAdminAppHostFixture fixture)
{
    [Theory]
    [InlineData("groupOfNames", "member", GroupMemberValueKind.DistinguishedName)]
    [InlineData("groupOfUniqueNames", "uniqueMember", GroupMemberValueKind.DistinguishedName)]
    [InlineData("posixGroup", "memberUid", GroupMemberValueKind.Uid)]
    public async Task Loaded_Schema_Confirms_The_Bundled_Membership_Forms(
        string objectClass, string attribute, GroupMemberValueKind kind)
    {
        using var cts = TestCancellation.Source();
        var schema = await fixture.Schema.GetSchemaAsync(cts.Token);
        Assert.True(schema.Available, schema.UnavailableReason);
        var entry = new LdapEntry("cn=test,dc=example,dc=org",
        [
            new LdapAttributeValues("objectClass", false, [objectClass], LdapValueClassification.Schema),
        ]);

        var detected = Assert.Single(GroupMembership.Detect(schema.Schema, entry));
        Assert.Equal(attribute, detected.Name);
        Assert.Equal(kind, detected.ValueKind);
    }

    [Theory]
    [InlineData("groupOfNames", "member", false)]
    [InlineData("groupOfUniqueNames", "uniqueMember", false)]
    [InlineData("posixGroup", "memberUid", true)]
    public async Task Supported_Group_Forms_Add_And_Remove_One_Exact_Value(
        string objectClass, string attribute, bool uidValued)
    {
        using var cts = TestCancellation.Source();
        var suffix = objectClass.ToLowerInvariant();
        var firstUid = $"gm-first-{suffix}";
        var secondUid = $"gm-second-{suffix}";
        var firstDn = fixture.DnUnder(Dn.Rdn("uid", firstUid), "ou=people");
        var secondDn = fixture.DnUnder(Dn.Rdn("uid", secondUid), "ou=people");
        var groupDn = fixture.DnUnder(Dn.Rdn("cn", $"gm-{suffix}"), "ou=groups");
        var initialValue = uidValued ? firstUid : firstDn;
        var addedValue = uidValued ? secondUid : secondDn;

        Assert.True((await fixture.Directory.AddEntryAsync(Person(firstDn, firstUid), cts.Token)).Succeeded);
        Assert.True((await fixture.Directory.AddEntryAsync(Person(secondDn, secondUid), cts.Token)).Succeeded);
        var groupAttributes = new List<LdapNewAttribute>
        {
            new("objectClass", [objectClass]),
            new("cn", [$"gm-{suffix}"]),
            new(attribute, [initialValue]),
        };
        if (uidValued)
        {
            groupAttributes.Add(new LdapNewAttribute("gidNumber", ["23001"]));
        }
        Assert.True((await fixture.Directory.AddEntryAsync(new LdapNewEntry(groupDn, groupAttributes), cts.Token)).Succeeded);

        try
        {
            var added = await fixture.Directory.ModifyEntryAsync(groupDn,
                [new LdapAttributeChange(DirectoryAttributeOperation.Add, attribute, [addedValue])], cts.Token);
            Assert.True(added.Succeeded, added.Message);
            var afterAdd = await fixture.Directory.GetEntryAsync(groupDn, [attribute], cts.Token);
            Assert.Contains(addedValue, Assert.Single(afterAdd!.Attributes).Values, StringComparer.Ordinal);

            var removed = await fixture.Directory.ModifyEntryAsync(groupDn,
                [new LdapAttributeChange(DirectoryAttributeOperation.Delete, attribute, [addedValue])], cts.Token);
            Assert.True(removed.Succeeded, removed.Message);
            var afterRemove = await fixture.Directory.GetEntryAsync(groupDn, [attribute], cts.Token);
            Assert.DoesNotContain(addedValue, Assert.Single(afterRemove!.Attributes).Values, StringComparer.Ordinal);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(groupDn, cts.Token);
            await fixture.Directory.DeleteEntryAsync(secondDn, cts.Token);
            await fixture.Directory.DeleteEntryAsync(firstDn, cts.Token);
        }
    }

    [Fact]
    public async Task Removing_The_Final_Required_Member_Surfaces_The_Server_Refusal()
    {
        using var cts = TestCancellation.Source();
        var personDn = fixture.DnUnder("uid=alice", "ou=people");
        var groupDn = fixture.DnUnder("cn=gm-final-member", "ou=groups");
        var created = await fixture.Directory.AddEntryAsync(new LdapNewEntry(groupDn,
        [
            new("objectClass", ["groupOfNames"]),
            new("cn", ["gm-final-member"]),
            new("member", [personDn]),
        ]), cts.Token);
        Assert.True(created.Succeeded, created.Message);

        try
        {
            var result = await fixture.Directory.ModifyEntryAsync(groupDn,
                [new LdapAttributeChange(DirectoryAttributeOperation.Delete, "member", [personDn])], cts.Token);

            Assert.False(result.Succeeded);
            Assert.Equal(LdapOperationStatus.SchemaViolation, result.Status);
            Assert.Equal(ResultCode.ObjectClassViolation, result.ResultCode);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(groupDn, cts.Token);
        }
    }

    [Fact]
    public async Task Escaped_Search_Input_Is_Literal_And_Cannot_Inject_A_Filter()
    {
        using var cts = TestCancellation.Source();
        var result = await fixture.Directory.SearchAsync(new LdapSearchOptions
        {
            Filter = GroupMemberSearch.Filter("alice*)(objectClass=*)"),
            Limit = GroupMemberSearch.ResultLimit,
            Attributes = ["uid"],
        }, cts.Token);

        Assert.Empty(result.Entries);
        Assert.False(result.Truncated);
    }

    private static LdapNewEntry Person(string dn, string uid) => new(dn,
    [
        new("objectClass", ["inetOrgPerson"]),
        new("uid", [uid]),
        new("cn", [uid]),
        new("sn", [uid]),
    ]);
}
