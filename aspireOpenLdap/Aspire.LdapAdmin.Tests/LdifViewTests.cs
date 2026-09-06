using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web.Components.Directory;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

/// <summary>
/// The LDIF view's pure logic: the import plan — what parses, what is refused before
/// anything runs, and what the plan table says about each record — plus the export filename.
/// The live export/import round-trip is <see cref="LdifViewLiveTests"/>.
/// </summary>
public sealed class LdifViewPlanTests
{
    [Fact]
    public void ParsePlan_Labels_Every_Record_Kind_With_Its_Detail_Count()
    {
        var plan = LdapLdifService.ParsePlan(
            "dn: uid=new,ou=people,dc=example,dc=org\n" +
            "changetype: add\n" +
            "objectClass: inetOrgPerson\n" +
            "cn: New Person\n" +
            "sn: Person\n" +
            "\n" +
            "dn: uid=bob,ou=people,dc=example,dc=org\n" +
            "changetype: modify\n" +
            "replace: mail\n" +
            "mail: bob@example.org\n" +
            "-\n" +
            "add: title\n" +
            "title: Engineer\n" +
            "-\n" +
            "\n" +
            "dn: uid=old,ou=people,dc=example,dc=org\n" +
            "changetype: delete\n" +
            "\n" +
            "dn: uid=move,ou=people,dc=example,dc=org\n" +
            "changetype: modrdn\n" +
            "newrdn: uid=moved\n" +
            "deleteoldrdn: 1\n");

        Assert.Null(plan.Error);
        Assert.Equal(
            [("add", 3), ("modify", 2), ("delete", 0), ("moddn", 0)],
            plan.Items.Select(static i => (i.ChangeType, i.DetailCount)));
        Assert.Equal("uid=bob,ou=people,dc=example,dc=org", plan.Items.ElementAt(1).Dn);
    }

    [Fact]
    public void ParsePlan_Reads_A_Content_Record_As_An_Add()
    {
        var plan = LdapLdifService.ParsePlan(
            "dn: uid=new,ou=people,dc=example,dc=org\n" +
            "objectClass: inetOrgPerson\n" +
            "cn: New Person\n");

        Assert.Null(plan.Error);
        var item = Assert.Single(plan.Items);
        Assert.Equal("add", item.ChangeType);
        Assert.Equal(2, item.DetailCount);
    }

    [Fact]
    public void ParsePlan_Refuses_Parse_Errors_Empty_Text_And_Increments()
    {
        Assert.Contains("parse", LdapLdifService.ParsePlan("this is not ldif").Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no records", LdapLdifService.ParsePlan("").Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("increment", LdapLdifService.ParsePlan(
            "dn: uid=bob,ou=people,dc=example,dc=org\n" +
            "changetype: modify\n" +
            "increment: uidNumber\n" +
            "uidNumber: 1\n" +
            "-\n").Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportFileName_Is_Filesystem_Safe_And_Falls_Back_To_The_Base_Dn()
    {
        Assert.Equal("ou-people-dc-example-dc-org.ldif", LdifPanel.ExportFileName("ou=people,dc=example,dc=org", "dc=example,dc=org"));
        Assert.Equal("dc-example-dc-org.ldif", LdifPanel.ExportFileName("", "dc=example,dc=org"));
    }

    public static TheoryData<string, string> ControlledChanges()
    {
        var data = new TheoryData<string, string>();
        foreach (var change in new[]
        {
            "changetype: add\nobjectClass: organizationalUnit\nou: controlled\n",
            "changetype: modify\nreplace: description\ndescription: changed\n-\n",
            "changetype: delete\n",
            "changetype: moddn\nnewrdn: ou=renamed\ndeleteoldrdn: 1\n",
            "changetype: modrdn\nnewrdn: ou=renamed\ndeleteoldrdn: 1\n",
        })
        {
            foreach (var criticality in new[] { "", " false", " true" })
            {
                data.Add(change, criticality);
            }
        }

        return data;
    }

    [Fact]
    public async Task Direct_Apply_Preflights_Increments_Using_The_Plan_Error()
    {
        const string ldif = "dn: ou=first,dc=example,dc=org\nchangetype: delete\n\n" +
            "dn: uid=second,dc=example,dc=org\nchangetype: modify\nincrement: uidNumber\nuidNumber: 1\n-\n";
        var plan = LdapLdifService.ParsePlan(ldif);
        Assert.Empty(plan.Items);
        Assert.Contains("increment", plan.Error, StringComparison.Ordinal);

        var items = LdifReader.Parse(ldif).Select(static r => new LdifImportItem(r, "ignored", r.Dn, 0)).ToArray();
        var result = await new LdapLdifService(null!).ApplyAsync(items);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Applied);
        Assert.Equal(2, result.Total);
        Assert.Equal(items[1].Record.Dn, result.FailedDn);
        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Outcome.Status);
        Assert.Equal(plan.Error, result.Outcome.Message);
    }

    [Theory]
    [MemberData(nameof(ControlledChanges))]
    public async Task Controls_Reject_The_Whole_Plan_And_Direct_Apply_Before_Directory_Access(string change, string criticality)
    {
        const string controlledDn = "ou=controlled,dc=example,dc=org";
        var ldif = "dn: ou=first,dc=example,dc=org\nchangetype: add\nobjectClass: organizationalUnit\nou: first\n\n" +
            $"dn: {controlledDn}\ncontrol: 1.2.840.113556.1.4.805{criticality}\n{change}";

        // Verify the pinned reader preserves the control rather than rejecting the fixture's syntax.
        var records = LdifReader.Parse(ldif);
        var controlled = Assert.IsAssignableFrom<LdifChangeRecord>(records[1]);
        var control = Assert.Single(controlled.Controls);
        Assert.Equal(criticality == "" ? (bool?)null : criticality == " true", control.Criticality);

        var plan = LdapLdifService.ParsePlan(ldif);
        Assert.Empty(plan.Items);
        Assert.Contains("controls are not supported", plan.Error, StringComparison.Ordinal);
        Assert.Contains(controlledDn, plan.Error, StringComparison.Ordinal);

        // Deliberately bypass ParsePlan. A null directory makes any dispatch, including
        // the supported first record, fail the test without touching a live server.
        var service = new LdapLdifService(null!);
        var result = await service.ApplyAsync(records.Select(static r => new LdifImportItem(r, "ignored", r.Dn, 0)).ToArray());

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Applied);
        Assert.Equal(2, result.Total);
        Assert.Equal(controlledDn, result.FailedDn);
        Assert.Equal(LdapOperationStatus.InvalidRequest, result.Outcome.Status);
        Assert.Equal(plan.Error, result.Outcome.Message);
    }
}

/// <summary>
/// The LDIF view against the live container: a subtree export that re-imports, and a
/// stop-on-first-failure apply whose progress report is exact.
/// </summary>
[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LdifViewLiveTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task Export_Orders_Parents_First_And_Round_Trips_Through_Import()
    {
        using var cts = TestCancellation.Source();
        var service = new LdapLdifService(fixture.Directory);
        var sourceOu = fixture.DnUnder(Dn.Rdn("ou", "ldif-src"));
        var targetOu = fixture.DnUnder(Dn.Rdn("ou", "ldif-dst"));

        var seeded = await service.ApplyAsync(MustPlan(
            $"dn: {sourceOu}\nobjectClass: organizationalUnit\nou: ldif-src\n\n" +
            $"dn: uid=exported,{sourceOu}\nobjectClass: inetOrgPerson\ncn: Exported Person\nsn: Person\nuid: exported\n"), cts.Token);
        Assert.True(seeded.Succeeded, seeded.Outcome.Message);

        try
        {
            var export = await service.ExportSubtreeAsync(sourceOu, cts.Token);
            Assert.False(export.Truncated);
            Assert.Equal(2, export.EntryCount);
            // Parent before child, so the export re-imports in order.
            Assert.True(
                export.Ldif.IndexOf($"dn: {sourceOu}", StringComparison.OrdinalIgnoreCase)
                    < export.Ldif.IndexOf("dn: uid=exported,", StringComparison.OrdinalIgnoreCase),
                export.Ldif);

            // Re-import the export under a fresh OU by textual rebase of the DNs.
            var rebased = export.Ldif.Replace(sourceOu, targetOu, StringComparison.OrdinalIgnoreCase);
            var applied = await service.ApplyAsync(MustPlan(rebased), cts.Token);
            Assert.True(applied.Succeeded, applied.Outcome.Message);

            var imported = await fixture.Directory.GetEntryAsync($"uid=exported,{targetOu}", cancellationToken: cts.Token);
            Assert.NotNull(imported);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(sourceOu, subtree: true, cts.Token);
            await fixture.Directory.DeleteEntryAsync(targetOu, subtree: true, cts.Token);
        }
    }

    [Fact]
    public async Task Apply_Stops_At_The_First_Failure_And_Reports_How_Far_It_Got()
    {
        using var cts = TestCancellation.Source();
        var service = new LdapLdifService(fixture.Directory);
        var first = fixture.DnUnder(Dn.Rdn("uid", "applies-first"), "ou=people");
        var missing = fixture.DnUnder(Dn.Rdn("uid", "does-not-exist"), "ou=people");
        var never = fixture.DnUnder(Dn.Rdn("uid", "never-created"), "ou=people");

        try
        {
            var result = await service.ApplyAsync(MustPlan(
                $"dn: {first}\nobjectClass: inetOrgPerson\ncn: Applies First\nsn: First\nuid: applies-first\n\n" +
                $"dn: {missing}\nchangetype: delete\n\n" +
                $"dn: {never}\nobjectClass: inetOrgPerson\ncn: Never Created\nsn: Never\nuid: never-created\n"), cts.Token);

            Assert.False(result.Succeeded);
            Assert.Equal(1, result.Applied);
            Assert.Equal(3, result.Total);
            Assert.Equal(missing, result.FailedDn);
            Assert.Equal(LdapOperationStatus.NotFound, result.Outcome.Status);

            // The record before the failure stays applied; the one after never ran.
            Assert.NotNull(await fixture.Directory.GetEntryAsync(first, cancellationToken: cts.Token));
            Assert.Null(await fixture.Directory.GetEntryAsync(never, cancellationToken: cts.Token));
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(first, cts.Token);
        }
    }

    private static IReadOnlyList<LdifImportItem> MustPlan(string ldif)
    {
        var plan = LdapLdifService.ParsePlan(ldif);
        Assert.Null(plan.Error);
        return plan.Items;
    }
}
