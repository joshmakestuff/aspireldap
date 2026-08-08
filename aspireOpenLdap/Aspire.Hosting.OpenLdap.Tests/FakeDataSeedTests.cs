using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Seeding;
using LdifDotNet;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Fast tests for <c>WithFakePeople</c>/<c>WithFakeGroups</c>/<c>WithFakeDirectory</c>: spec
/// accumulation at builder time, deferred materialization (invoked directly, as the
/// start-time hook does), and the documented contracts (determinism, OU auto-declare,
/// no <c>userPassword</c>).
/// </summary>
public class FakeDataSeedTests
{
    private static List<LdifRecord> Materialize(IResourceBuilder<OpenLdapResource> ldap)
    {
        OpenLdapResourceBuilderExtensions.MaterializeFakeDataSpecs(ldap.Resource);
        return ldap.Resource.SeedRecords!;
    }

    private static IEnumerable<LdifContentRecord> OfObjectClass(IEnumerable<LdifRecord> records, string objectClass)
        => records.OfType<LdifContentRecord>()
            .Where(r => r["objectClass"]?.Values.Any(v => v.AsString() == objectClass) == true);

    [Fact]
    public void WithFakePeople_Defers_Then_Materializes_Under_The_Ou()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap").WithFakePeople(7, seed: 1);

        // Builder time: a spec and the records mount, but no records yet.
        var spec = Assert.Single(ldap.Resource.FakeDataSpecs!);
        Assert.Equal(new FakeDataSpec(FakeDataKind.People, 7, "people", 1), spec);
        Assert.Empty(ldap.Resource.SeedRecords!);
        Assert.Single(
            ldap.Resource.Annotations.OfType<ContainerMountAnnotation>(),
            m => m.Target == "/ldifs/01-aspire-seed-records.ldif");

        var records = Materialize(ldap);

        Assert.Equal(7, records.Count);
        Assert.All(records.OfType<LdifContentRecord>(), r =>
            Assert.EndsWith(",ou=people,dc=example,dc=org", r.Dn));
        Assert.Equal(7, OfObjectClass(records, "inetOrgPerson").Count());
        Assert.Null(ldap.Resource.FakeDataSpecs); // consumed
    }

    [Fact]
    public void WithBaseDn_After_WithFakePeople_Is_Honored()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithFakePeople(3, seed: 1)
            .WithBaseDn("dc=late,dc=org");

        var records = Materialize(ldap);

        Assert.All(records.OfType<LdifContentRecord>(), r =>
            Assert.EndsWith(",ou=people,dc=late,dc=org", r.Dn));
    }

    [Fact]
    public void Ou_Is_Auto_Declared_Once_And_Passes_Validation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap").WithFakePeople(2, seed: 1);

        var model = ldap.Resource.SeedModel!;
        Assert.Equal("people", Assert.Single(model.OrganizationalUnits).Name);
        LdapSeedValidator.Validate(ldap.Resource, model); // no duplicate, no undeclared refs

        // The typed-seed route emits the base-DN root, so a fake-only chain has a full
        // parent tree: root, then the OU.
        var ldif = LdapSeedLdifGenerator.Generate(ldap.Resource, model);
        var entries = LdifReader.Parse(ldif).OfType<LdifContentRecord>().Select(r => r.Dn).ToArray();
        Assert.Equal(["dc=example,dc=org", "ou=people,dc=example,dc=org"], entries);
    }

    [Fact]
    public void Prior_User_Declared_Ou_Is_Not_Duplicated()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithOrganizationalUnit("People") // case differs; validator compares OrdinalIgnoreCase
            .WithFakePeople(2, seed: 1);

        var model = ldap.Resource.SeedModel!;
        Assert.Equal("People", Assert.Single(model.OrganizationalUnits).Name);
        LdapSeedValidator.Validate(ldap.Resource, model);
    }

    [Fact]
    public void Same_Seed_Produces_Byte_Identical_Ldif()
    {
        static string Generate()
        {
            var builder = DistributedApplication.CreateBuilder();
            var ldap = builder.AddOpenLdap("ldap").WithFakeDirectory(people: 10, groups: 3, seed: 42);
            return LdifWriter.WriteToString(Materialize(ldap), LdapSeedLdifGenerator.WriterOptions);
        }

        var first = Generate();
        var second = Generate();

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void WithFakeGroups_Without_People_Throws_Eagerly()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddOpenLdap("ldap").WithFakeGroups(2));

        Assert.Contains("WithFakePeople", ex.Message);
        Assert.Contains("WithFakeDirectory", ex.Message);
    }

    [Fact]
    public void Group_Members_Are_Fake_Person_Dns()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithFakePeople(8, seed: 5)
            .WithFakeGroups(3, seed: 5);

        var records = Materialize(ldap);
        var personDns = OfObjectClass(records, "inetOrgPerson").Select(r => r.Dn).ToHashSet(StringComparer.Ordinal);
        var groups = OfObjectClass(records, "groupOfNames").ToArray();

        Assert.Equal(3, groups.Length);
        Assert.All(groups, g =>
        {
            Assert.EndsWith(",ou=groups,dc=example,dc=org", g.Dn);
            var members = g["member"]!.Values.Select(v => v.AsString()).ToArray();
            Assert.NotEmpty(members);
            Assert.All(members, m => Assert.Contains(m, personDns));
        });
    }

    [Fact]
    public void Unsafe_Ou_Name_Throws_At_The_Fluent_Call()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ex = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddOpenLdap("ldap").WithFakePeople(2, ou: "bad ou!"));

        Assert.Contains("[A-Za-z0-9._-]+", ex.Message);
    }

    [Fact]
    public void WithFakeDirectory_Composes_People_Then_Groups()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap").WithFakeDirectory(seed: 9);

        Assert.Equal(
            [
                new FakeDataSpec(FakeDataKind.People, 25, "people", 9),
                new FakeDataSpec(FakeDataKind.Groups, 4, "groups", 9),
            ],
            ldap.Resource.FakeDataSpecs!);
        Assert.Equal(
            ["people", "groups"],
            ldap.Resource.SeedModel!.OrganizationalUnits.Select(o => o.Name).ToArray());

        var records = Materialize(ldap);
        Assert.Equal(29, records.Count);
    }

    [Fact]
    public void Generated_People_Have_No_UserPassword()
    {
        // Witnesses the AGENTS.md claim: fake people are searchable data, not bindable
        // accounts. Pins the claim against future LdifDotNet.Generator bumps.
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap").WithFakeDirectory(seed: 7);

        var records = Materialize(ldap);

        Assert.Equal(29, records.Count);
        Assert.All(records.OfType<LdifContentRecord>(), r => Assert.Null(r["userPassword"]));
    }

    [Fact]
    public void DanglingMemberRatio_Emits_Members_Outside_The_Pool()
    {
        // Witnesses the docs/fake-data.md claim about LdifGeneratorOptions.DanglingMemberRatio,
        // which only exists from LdifDotNet.Generator 0.7.0 — the guide documented it while the
        // pinned package was 0.6.0. Fails to compile if the bump is ever reverted.
        var people = new LdifDotNet.Generator.LdifGenerator(
            new LdifDotNet.Generator.LdifGeneratorOptions { Seed = 3 })
            .People(10, "ou=people,dc=example,dc=org");
        var pool = people.Select(p => p.Dn).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var groups = new LdifDotNet.Generator.LdifGenerator(
            new LdifDotNet.Generator.LdifGeneratorOptions { Seed = 3, DanglingMemberRatio = 1.0 })
            .Groups(5, "ou=groups,dc=example,dc=org", people);

        var members = groups.SelectMany(g => g["member"]!.Values.Select(v => v.AsString())).ToList();
        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.DoesNotContain(m, pool));
    }

    [Fact]
    public void Schema_Generated_Dn_Attributes_Resolve_To_Generated_Entries()
    {
        // Witnesses the docs/fake-data.md claim that schema-driven DN attributes point at real
        // entries, which needs LdifDotNet.Generator 0.8.0 (ldifdotnet#68). On 0.7.0 every value
        // was the entry's own parent DN. Uses SchemaGeneratorOptions.DnPool, so this also fails
        // to compile if the bump is reverted.
        var schema = LdifDotNet.Schema.LdapSchema.Parse(
            "attributetype ( 1.2.3.9.1 NAME 'cn' SYNTAX 1.3.6.1.4.1.1466.115.121.1.15 )\n" +
            "attributetype ( 1.2.3.9.2 NAME 'member' SYNTAX 1.3.6.1.4.1.1466.115.121.1.12 )\n" +
            "objectclass ( 1.2.3.9.9 NAME 'team' STRUCTURAL MUST ( cn $ member ) )\n");

        const string peopleDn = "ou=people,dc=example,dc=org";
        var pool = Enumerable.Range(1, 20).Select(i => $"uid=person{i},{peopleDn}").ToList();

        var options = new LdifDotNet.Generator.SchemaGeneratorOptions { Seed = 5, OptionalAttributeFill = 0 };
        options.DnPool["member"] = pool;
        var groups = new LdifDotNet.Generator.SchemaEntryGenerator(schema, options)
            .Entries("team", 10, "ou=groups,dc=example,dc=org");

        var members = groups.SelectMany(g => g["member"]!.Values.Select(v => v.AsString())).ToList();
        Assert.NotEmpty(members);
        // Real membership, not the container: the 0.7.0 behaviour would fail both of these.
        Assert.All(members, m => Assert.Contains(m, pool, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("ou=groups,dc=example,dc=org", members, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(groups, g => g["member"]!.Values.Count > 1);
    }

    [Fact]
    public void Materializer_Without_Specs_Is_A_No_Op()
    {
        // Pins the WithSeedRecords pipeline refactor: pure record seeding never sees
        // fake-data materialization side effects.
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithSeedRecords(new LdifContentRecord("dc=example,dc=org",
                new LdifAttribute("objectClass", "organization"),
                new LdifAttribute("o", "example")));

        OpenLdapResourceBuilderExtensions.MaterializeFakeDataSpecs(ldap.Resource);

        Assert.Single(ldap.Resource.SeedRecords!);
        Assert.Null(ldap.Resource.SeedModel); // no OU auto-declare either
    }

    [Fact]
    public void Second_Materialization_Adds_Nothing()
    {
        // The start-time hook re-fires on an in-run container restart; consumed specs must
        // not regenerate (which would duplicate DNs and fail the seed load).
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap").WithFakePeople(4, seed: 3);

        var afterFirst = Materialize(ldap).Count;
        var afterSecond = Materialize(ldap).Count;

        Assert.Equal(4, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Explicit_Seed_Records_Precede_Fake_Records_In_The_File()
    {
        var builder = DistributedApplication.CreateBuilder();
        var ldap = builder.AddOpenLdap("ldap")
            .WithFakePeople(2, seed: 1)
            .WithSeedRecords(new LdifContentRecord("ou=custom,dc=example,dc=org",
                new LdifAttribute("objectClass", "organizationalUnit"),
                new LdifAttribute("ou", "custom")));

        var records = Materialize(ldap);

        // Explicit records accumulate at builder time; fake records append at start time —
        // so explicit ones always load first, as the XML docs state.
        Assert.Equal(3, records.Count);
        Assert.Equal("ou=custom,dc=example,dc=org", Assert.IsType<LdifContentRecord>(records[0]).Dn);
    }
}
