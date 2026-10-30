namespace OpsFlow.Infrastructure.Configuration;

/// <summary>
/// Options controlling development/test data seeding, bound from the "Seed"
/// configuration section. The default password is supplied only through user
/// secrets or environment variables — never committed.
/// </summary>
public sealed class SeedOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Seed";

    /// <summary>Whether seeding is enabled (only honored in Development).</summary>
    public bool Enabled { get; set; }

    /// <summary>Password assigned to privileged seeded users (admin, coordinator, technician). Never logged or returned.</summary>
    public string? DefaultPassword { get; set; }

    /// <summary>
    /// Password assigned to the read-only Viewer account, so it can be shared
    /// publicly in a demo without exposing privileged accounts. Null, empty and
    /// whitespace all mean "not configured". In Development seeding an unset
    /// value falls back to <see cref="DefaultPassword"/>; public demo seeding
    /// (<c>SEED_DEMO_DATA=true</c>) requires it. When set, it must always differ
    /// from <see cref="DefaultPassword"/>.
    /// </summary>
    public string? ViewerPassword { get; set; }
}
