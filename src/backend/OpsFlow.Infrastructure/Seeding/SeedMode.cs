namespace OpsFlow.Infrastructure.Seeding;

/// <summary>Identifies the startup path that runs the seeder, which sets how strict credential validation is.</summary>
public enum SeedMode
{
    /// <summary>
    /// Local Development or Testing. An unset viewer password falls back to the
    /// default password for convenience.
    /// </summary>
    Development,

    /// <summary>
    /// Public demo seeding in Production (<c>SEED_DEMO_DATA=true</c>). Both
    /// passwords are required and must differ, so the publicly shared Viewer
    /// credential can never unlock a privileged account.
    /// </summary>
    PublicDemo,
}
