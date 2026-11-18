using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.Seeding;

// A class rather than a record: a record's generated ToString would print the passwords.
internal sealed class SeedCredentials
{
    private SeedCredentials(string privilegedPassword, string viewerPassword)
    {
        PrivilegedPassword = privilegedPassword;
        ViewerPassword = viewerPassword;
    }

    public string PrivilegedPassword { get; }

    public string ViewerPassword { get; }

    /// <summary>
    /// Validates the seed passwords for <paramref name="mode"/>. Returns <c>null</c>
    /// when user seeding should be skipped. Error messages name settings, never values.
    /// </summary>
    public static SeedCredentials? Resolve(SeedOptions options, SeedMode mode)
    {
        var privileged = Normalize(options.DefaultPassword);
        var viewer = Normalize(options.ViewerPassword);

        if (mode == SeedMode.PublicDemo)
        {
            if (privileged is null)
            {
                throw ConfigurationError("Seed:DefaultPassword is required for public demo seeding.");
            }

            if (viewer is null)
            {
                throw ConfigurationError(
                    "Seed:ViewerPassword is required for public demo seeding so the public Viewer " +
                    "account never shares the privileged password.");
            }
        }

        if (privileged is null)
        {
            return null;
        }

        if (viewer is not null && string.Equals(viewer, privileged, StringComparison.Ordinal))
        {
            throw ConfigurationError(
                "Seed:ViewerPassword must differ from Seed:DefaultPassword. Using the same password " +
                "for the viewer account and privileged accounts is not permitted.");
        }

        return new SeedCredentials(privileged, viewer ?? privileged);
    }

    // Compose substitutes unset variables with "", so blank must mean "not configured".
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static InvalidOperationException ConfigurationError(string detail) =>
        new($"Seed configuration error: {detail}");
}
