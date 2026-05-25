namespace Aspire.Hosting.ApplicationModel.Seeding;

internal sealed record OrganizationalUnitEntry(string Name);

internal sealed record SeedUserEntry(
    string Uid,
    string Password,
    string? OrganizationalUnit,
    string Cn,
    string Sn,
    string? Mail);

internal sealed record SeedGroupEntry(
    string Cn,
    IReadOnlyList<string> Members,
    string? OrganizationalUnit);

internal sealed class LdapSeedModel
{
    public List<OrganizationalUnitEntry> OrganizationalUnits { get; } = new();
    public List<SeedUserEntry> Users { get; } = new();
    public List<SeedGroupEntry> Groups { get; } = new();

    public bool IsEmpty =>
        OrganizationalUnits.Count == 0 && Users.Count == 0 && Groups.Count == 0;
}
