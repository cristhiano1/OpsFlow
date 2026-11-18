using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Seeding;

namespace OpsFlow.Infrastructure.UnitTests.Seeding;

public sealed class SeedCredentialsTests
{
    private const string Privileged = "Privileged-Pa55word!";
    private const string Viewer = "Viewer-Pa55word!";

    private static SeedOptions Options(string? defaultPassword, string? viewerPassword) =>
        new() { Enabled = true, DefaultPassword = defaultPassword, ViewerPassword = viewerPassword };

    // ── Development ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Development_unset_viewer_password_falls_back_to_default(string? viewerPassword)
    {
        var credentials = SeedCredentials.Resolve(Options(Privileged, viewerPassword), SeedMode.Development);

        Assert.NotNull(credentials);
        Assert.Equal(Privileged, credentials.PrivilegedPassword);
        Assert.Equal(Privileged, credentials.ViewerPassword);
    }

    [Fact]
    public void Development_distinct_viewer_password_is_used_for_viewer()
    {
        var credentials = SeedCredentials.Resolve(Options(Privileged, Viewer), SeedMode.Development);

        Assert.NotNull(credentials);
        Assert.Equal(Privileged, credentials.PrivilegedPassword);
        Assert.Equal(Viewer, credentials.ViewerPassword);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Development_without_default_password_skips_user_seeding(string? defaultPassword)
    {
        Assert.Null(SeedCredentials.Resolve(Options(defaultPassword, Viewer), SeedMode.Development));
    }

    [Theory]
    [InlineData(SeedMode.Development)]
    [InlineData(SeedMode.PublicDemo)]
    public void Equal_viewer_and_default_password_is_rejected_in_every_mode(SeedMode mode)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SeedCredentials.Resolve(Options(Privileged, Privileged), mode));

        Assert.Contains("Seed:ViewerPassword must differ from Seed:DefaultPassword", ex.Message);
        Assert.DoesNotContain(Privileged, ex.Message);
    }

    [Fact]
    public void Viewer_password_differing_only_by_case_is_treated_as_distinct()
    {
        var credentials = SeedCredentials.Resolve(
            Options(Privileged, Privileged.ToUpperInvariant()), SeedMode.Development);

        Assert.NotNull(credentials);
        Assert.Equal(Privileged.ToUpperInvariant(), credentials.ViewerPassword);
    }

    // ── Public demo ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PublicDemo_requires_viewer_password(string? viewerPassword)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SeedCredentials.Resolve(Options(Privileged, viewerPassword), SeedMode.PublicDemo));

        Assert.Contains("Seed:ViewerPassword is required for public demo seeding", ex.Message);
        Assert.DoesNotContain(Privileged, ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PublicDemo_requires_default_password(string? defaultPassword)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SeedCredentials.Resolve(Options(defaultPassword, Viewer), SeedMode.PublicDemo));

        Assert.Contains("Seed:DefaultPassword is required for public demo seeding", ex.Message);
        Assert.DoesNotContain(Viewer, ex.Message);
    }

    [Fact]
    public void PublicDemo_distinct_passwords_are_accepted()
    {
        var credentials = SeedCredentials.Resolve(Options(Privileged, Viewer), SeedMode.PublicDemo);

        Assert.NotNull(credentials);
        Assert.Equal(Privileged, credentials.PrivilegedPassword);
        Assert.Equal(Viewer, credentials.ViewerPassword);
    }

    [Fact]
    public void Credentials_ToString_does_not_expose_passwords()
    {
        var credentials = SeedCredentials.Resolve(Options(Privileged, Viewer), SeedMode.PublicDemo);

        Assert.NotNull(credentials);
        var text = credentials.ToString();
        Assert.DoesNotContain(Privileged, text);
        Assert.DoesNotContain(Viewer, text);
    }
}
