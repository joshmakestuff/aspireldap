using System.DirectoryServices.Protocols;
using Aspire.LdapAdmin.Core;
using Aspire.OpenLdap;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class BindPasswordGuardTests
{
    private const string BindDn = "uid=operator,dc=example,dc=org";

    public static TheoryData<string, DirectoryAttributeOperation> PasswordModifications
    {
        get
        {
            TheoryData<string, DirectoryAttributeOperation> cases = new();
            foreach (var name in new[] { "userPassword", "USERPASSWORD", "2.5.4.35", "userPassword;binary", "2.5.4.35;binary" })
            {
                foreach (var operation in new[] { DirectoryAttributeOperation.Add, DirectoryAttributeOperation.Replace, DirectoryAttributeOperation.Delete })
                {
                    cases.Add(name, operation);
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(PasswordModifications))]
    public async Task Password_names_oids_and_options_are_refused_before_dispatch(string name, DirectoryAttributeOperation operation)
    {
        var result = await Directory().ModifyEntryAsync(BindDn.ToUpperInvariant(),
            [new LdapAttributeChange(operation, name, ["replacement"])], new CancellationToken(true));

        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Status);
        Assert.Null(result.ResultCode);
        Assert.Contains("bind identity", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("description", BindDn)]
    [InlineData("2.5.4.350", BindDn)]
    [InlineData("userPassword", "uid=someone-else,dc=example,dc=org")]
    public async Task Unrelated_modifications_are_not_refused_by_the_password_guard(string name, string dn)
    {
        var result = await Directory().ModifyEntryAsync(dn,
            [new LdapAttributeChange(DirectoryAttributeOperation.Replace, name, ["value"])], new CancellationToken(true));

        Assert.Equal(LdapOperationStatus.Cancelled, result.Status);
    }

    [Theory]
    [InlineData("userPassword")]
    [InlineData("2.5.4.35")]
    [InlineData("2.5.4.35;binary")]
    public async Task Ldif_import_uses_the_same_password_guard(string name)
    {
        var plan = LdapLdifService.ParsePlan($"dn: {BindDn}\nchangetype: modify\nreplace: {name}\n{name}: replacement\n-\n");
        Assert.Null(plan.Error);
        var result = await new LdapLdifService(Directory()).ApplyAsync(plan.Items);

        Assert.Equal(0, result.Applied);
        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Outcome.Status);
        Assert.Contains("bind identity", result.Outcome.Message, StringComparison.Ordinal);
    }

    private static LdapDirectoryService Directory()
    {
        var connection = new OpenLdapConnectionStringBuilder
        {
            Endpoint = new Uri("ldap://127.0.0.1:1"),
            BaseDn = "dc=example,dc=org",
            BindDn = BindDn,
            BindPassword = "unused",
        };
        var factory = new OpenLdapClientFactory(connection,
            new OpenLdapClientSettings { ConnectionString = connection.Build(), Timeout = TimeSpan.FromSeconds(1) });
        return new LdapDirectoryService(factory, new LdapSchemaService(factory, NullLogger<LdapSchemaService>.Instance));
    }
}
