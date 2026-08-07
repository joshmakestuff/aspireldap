namespace Aspire.Hosting.ApplicationModel.Seeding;

/// <summary>Which generator call a <see cref="FakeDataSpec"/> defers.</summary>
internal enum FakeDataKind
{
    People,
    Groups,
}

/// <summary>
/// A deferred fake-data request declared via <c>WithFakePeople</c>/<c>WithFakeGroups</c>.
/// Materialized into LDIF records at <c>BeforeResourceStarted</c> time, when the final
/// base DN is known.
/// </summary>
internal sealed record FakeDataSpec(FakeDataKind Kind, int Count, string Ou, int? Seed);
