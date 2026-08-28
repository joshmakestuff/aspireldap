using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// Pure tests for the per-entry LDIF surface: the entry renders as one content
/// record, and an edited draft diffs back into exactly the changes that make the entry
/// match — with everything the diff cannot honestly apply refused, never guessed at.
/// </summary>
public sealed class EntryLdifTests
{
    private static LdapEntry Entry(params LdapAttributeValues[] attributes) =>
        new("uid=alice.chen,ou=people,dc=aspire,dc=dev", attributes);

    private static LdapAttributeValues Text(string name, params string[] values) =>
        new(name, IsBinary: false, values, LdapValueClassification.Schema);

    private static LdapAttributeValues Binary(string name, params string[] base64Values) =>
        new(name, IsBinary: true, base64Values, LdapValueClassification.Schema);

    // ---- Write --------------------------------------------------------------------------

    [Fact]
    public void Write_Puts_ObjectClass_First_And_Base64_Encodes_Binary()
    {
        var photo = Convert.ToBase64String([0xFF, 0xD8, 0x00]);
        var ldif = LdapEntryLdif.Write(Entry(
            Text("uid", "alice.chen"),
            Text("objectClass", "inetOrgPerson", "posixAccount"),
            Binary("jpegPhoto", photo)));

        var lines = ldif.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal("dn: uid=alice.chen,ou=people,dc=aspire,dc=dev", lines[0]);
        Assert.Equal("objectClass: inetOrgPerson", lines[1]);
        Assert.Equal("objectClass: posixAccount", lines[2]);
        Assert.Contains("uid: alice.chen", lines);
        Assert.Contains($"jpegPhoto:: {photo}", lines);
        Assert.DoesNotContain(lines, static l => l.StartsWith("version", StringComparison.Ordinal));
    }

    // ---- Diff ---------------------------------------------------------------------------

    [Fact]
    public void Diff_Of_Unedited_Draft_Is_Empty()
    {
        var entry = Entry(
            Text("objectClass", "inetOrgPerson"),
            Text("cn", "Alice Chen"),
            Binary("jpegPhoto", Convert.ToBase64String([1, 2, 3])));

        var diff = LdapEntryLdif.Diff(entry, LdapEntryLdif.Write(entry));

        Assert.Null(diff.Error);
        Assert.NotNull(diff.Changes);
        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void Diff_Ignores_Value_Order_Within_An_Attribute()
    {
        var entry = Entry(Text("objectClass", "inetOrgPerson", "posixAccount"));

        var diff = LdapEntryLdif.Diff(entry,
            "dn: uid=alice.chen,ou=people,dc=aspire,dc=dev\n" +
            "objectClass: posixAccount\n" +
            "objectClass: inetOrgPerson\n");

        Assert.Null(diff.Error);
        Assert.Empty(diff.Changes!);
    }

    [Fact]
    public void Diff_Turns_An_Edited_Value_Into_One_Replace()
    {
        var entry = Entry(Text("objectClass", "inetOrgPerson"), Text("cn", "Alice Chen"));

        var diff = LdapEntryLdif.Diff(entry,
            "dn: uid=alice.chen,ou=people,dc=aspire,dc=dev\n" +
            "objectClass: inetOrgPerson\n" +
            "cn: Alice A. Chen\n");

        var change = Assert.Single(diff.Changes!);
        Assert.Equal(DirectoryAttributeOperation.Replace, change.Operation);
        Assert.Equal("cn", change.Name);
        Assert.Equal(["Alice A. Chen"], change.Values);
        Assert.False(change.IsBase64);
    }

    [Fact]
    public void Diff_Turns_A_Removed_Attribute_Into_A_Delete()
    {
        var entry = Entry(Text("objectClass", "inetOrgPerson"), Text("description", "temp"));

        var diff = LdapEntryLdif.Diff(entry,
            "dn: uid=alice.chen,ou=people,dc=aspire,dc=dev\n" +
            "objectClass: inetOrgPerson\n");

        var change = Assert.Single(diff.Changes!);
        Assert.Equal(DirectoryAttributeOperation.Delete, change.Operation);
        Assert.Equal("description", change.Name);
        Assert.Empty(change.Values);
    }

    [Fact]
    public void Diff_Turns_An_Added_Attribute_Into_A_Replace()
    {
        var entry = Entry(Text("objectClass", "inetOrgPerson"));

        var diff = LdapEntryLdif.Diff(entry,
            "dn: uid=alice.chen,ou=people,dc=aspire,dc=dev\n" +
            "objectClass: inetOrgPerson\n" +
            "mail: alice@aspire.dev\n");

        var change = Assert.Single(diff.Changes!);
        Assert.Equal(DirectoryAttributeOperation.Replace, change.Operation);
        Assert.Equal("mail", change.Name);
        Assert.Equal(["alice@aspire.dev"], change.Values);
    }

    [Fact]
    public void Diff_Keeps_Binary_Attributes_Base64()
    {
        var oldPhoto = Convert.ToBase64String([1, 2, 3]);
        var newPhoto = Convert.ToBase64String([4, 5, 6]);
        var entry = Entry(Text("objectClass", "inetOrgPerson"), Binary("jpegPhoto", oldPhoto));

        var diff = LdapEntryLdif.Diff(entry,
            "dn: uid=alice.chen,ou=people,dc=aspire,dc=dev\n" +
            "objectClass: inetOrgPerson\n" +
            $"jpegPhoto:: {newPhoto}\n");

        var change = Assert.Single(diff.Changes!);
        Assert.Equal("jpegPhoto", change.Name);
        Assert.True(change.IsBase64);
        Assert.Equal([newPhoto], change.Values);
    }

    [Fact]
    public void Diff_Merges_Repeated_Attribute_Lines_Case_Insensitively()
    {
        var entry = Entry(Text("objectClass", "inetOrgPerson"));

        var diff = LdapEntryLdif.Diff(entry,
            "dn: uid=alice.chen,ou=people,dc=aspire,dc=dev\n" +
            "objectClass: inetOrgPerson\n" +
            "mail: a@aspire.dev\n" +
            "MAIL: b@aspire.dev\n");

        var change = Assert.Single(diff.Changes!);
        Assert.Equal(2, change.Values.Count);
    }

    // ---- Diff refusals ------------------------------------------------------------------

    [Theory]
    [InlineData("", "empty")]
    [InlineData("dn: uid=someone.else,ou=people,dc=aspire,dc=dev\nobjectClass: top\n", "DN")]
    [InlineData("dn: uid=alice.chen,ou=people,dc=aspire,dc=dev\nchangetype: modify\nreplace: cn\ncn: X\n-\n", "content record")]
    [InlineData("dn: uid=alice.chen,ou=people,dc=aspire,dc=dev\ncn: A\n\ndn: uid=b,ou=people,dc=aspire,dc=dev\ncn: B\n", "one record")]
    public void Diff_Refuses_What_It_Cannot_Honestly_Apply(string draft, string reasonFragment)
    {
        var diff = LdapEntryLdif.Diff(Entry(Text("cn", "Alice Chen")), draft);

        Assert.Null(diff.Changes);
        Assert.NotNull(diff.Error);
        Assert.Contains(reasonFragment, diff.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diff_Reports_Parse_Errors_As_Errors()
    {
        var diff = LdapEntryLdif.Diff(Entry(Text("cn", "Alice Chen")), "this is not ldif at all");

        Assert.Null(diff.Changes);
        Assert.Contains("parse", diff.Error, StringComparison.OrdinalIgnoreCase);
    }
}
