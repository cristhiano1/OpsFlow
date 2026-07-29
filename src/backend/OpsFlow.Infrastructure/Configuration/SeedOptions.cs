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

    /// <summary>Password assigned to seeded development users. Never logged or returned.</summary>
    public string? DefaultPassword { get; set; }
}
