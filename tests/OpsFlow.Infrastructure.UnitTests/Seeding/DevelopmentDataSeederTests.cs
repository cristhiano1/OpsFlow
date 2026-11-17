using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Identity;
using OpsFlow.Infrastructure.Observability;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Seeding;
using OpsFlow.Infrastructure.UnitTests.TestSupport;

namespace OpsFlow.Infrastructure.UnitTests.Seeding;

// Upgrade paths for existing databases, on EF InMemory with real ASP.NET Identity.
// SeedingTests (integration) covers the same flow against SQL Server.
public sealed class DevelopmentDataSeederTests
{
    private const string ViewerEmail = "viewer@opsflow.local";
    private static readonly Guid NorthwindId = new("11111111-1111-1111-1111-111111111111");

    private static readonly string[] PrivilegedEmails =
    [
        "admin@opsflow.local",
        "coordinator@opsflow.local",
        "technician@opsflow.local",
        "admin@contoso.local",
    ];

    private readonly string _databaseName = "seeder-" + Guid.NewGuid();

    private static string NewPassword() => "Aa1!" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Existing_viewer_on_shared_password_moves_to_newly_configured_viewer_password()
    {
        var privileged = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(privileged, viewerPassword: null);
        Assert.True(await AcceptsAsync(ViewerEmail, privileged));

        await SeedAsync(privileged, viewer);

        Assert.True(await AcceptsAsync(ViewerEmail, viewer));
        Assert.False(await AcceptsAsync(ViewerEmail, privileged));
        foreach (var email in PrivilegedEmails)
        {
            Assert.True(await AcceptsAsync(email, privileged));
        }
    }

    [Fact]
    public async Task Updating_a_password_rotates_the_security_stamp()
    {
        var privileged = NewPassword();
        await SeedAsync(privileged, viewerPassword: null);
        var before = await SecurityStampAsync(ViewerEmail);

        await SeedAsync(privileged, NewPassword());

        Assert.NotEqual(before, await SecurityStampAsync(ViewerEmail));
    }

    [Fact]
    public async Task Reseeding_with_unchanged_passwords_leaves_accounts_untouched()
    {
        var privileged = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(privileged, viewer);
        var viewerStamp = await SecurityStampAsync(ViewerEmail);
        var adminStamp = await SecurityStampAsync(PrivilegedEmails[0]);

        await SeedAsync(privileged, viewer);

        Assert.Equal(viewerStamp, await SecurityStampAsync(ViewerEmail));
        Assert.Equal(adminStamp, await SecurityStampAsync(PrivilegedEmails[0]));
    }

    [Fact]
    public async Task PublicDemo_refuses_when_existing_viewer_still_accepts_the_default_password()
    {
        var legacyShared = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(legacyShared, viewerPassword: null);
        var stampBefore = await SecurityStampAsync(ViewerEmail);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SeedAsync(legacyShared, viewer, SeedMode.PublicDemo));

        Assert.Contains("Choose a new Seed:DefaultPassword", ex.Message);
        Assert.DoesNotContain(legacyShared, ex.Message);
        Assert.DoesNotContain(viewer, ex.Message);
        Assert.True(await AcceptsAsync(ViewerEmail, legacyShared));
        Assert.Equal(stampBefore, await SecurityStampAsync(ViewerEmail));
    }

    [Fact]
    public async Task PublicDemo_with_rotated_default_password_retires_the_old_shared_credential()
    {
        var legacyShared = NewPassword();
        var privileged = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(legacyShared, viewerPassword: null);

        await SeedAsync(privileged, viewer, SeedMode.PublicDemo);

        Assert.True(await AcceptsAsync(ViewerEmail, viewer));
        Assert.False(await AcceptsAsync(ViewerEmail, legacyShared));
        foreach (var email in PrivilegedEmails)
        {
            Assert.True(await AcceptsAsync(email, privileged));
            Assert.False(await AcceptsAsync(email, legacyShared));
            Assert.False(await AcceptsAsync(email, viewer));
        }
    }

    [Fact]
    public async Task Rotating_the_default_password_updates_existing_privileged_accounts()
    {
        var first = NewPassword();
        var second = NewPassword();
        var viewer = NewPassword();
        await SeedAsync(first, viewer, SeedMode.PublicDemo);

        await SeedAsync(second, viewer, SeedMode.PublicDemo);

        foreach (var email in PrivilegedEmails)
        {
            Assert.True(await AcceptsAsync(email, second));
            Assert.False(await AcceptsAsync(email, first));
        }

        Assert.True(await AcceptsAsync(ViewerEmail, viewer));
    }

    [Fact]
    public async Task Configured_password_that_fails_policy_does_not_change_the_existing_account()
    {
        var privileged = NewPassword();
        await SeedAsync(privileged, viewerPassword: null);
        const string weak = "weakpassword";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(weak, viewerPassword: null));

        Assert.DoesNotContain(weak, ex.Message);
        Assert.True(await AcceptsAsync(PrivilegedEmails[0], privileged));
    }

    // ── Demo metric timestamps ──────────────────────────────────────────

    [Theory]
    [InlineData("2026-10-30T00:00:00Z")]
    [InlineData("2026-10-30T00:07:00Z")]
    [InlineData("2026-10-30T07:59:00Z")]
    [InlineData("2026-10-30T12:00:00Z")]
    [InlineData("2026-10-30T20:00:00Z")]
    [InlineData("2026-10-30T23:59:59Z")]
    public async Task Demo_metrics_are_never_in_the_future_and_cover_the_last_30_utc_days(string seededAt)
    {
        var now = DateTimeOffset.Parse(seededAt, CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        await SeedAsync(NewPassword(), NewPassword(), now: now);
        var metrics = await DemoMetricsAsync();

        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.True(m.RecordedAt <= now));

        var days = metrics.Select(m => DateOnly.FromDateTime(m.RecordedAt.UtcDateTime)).Distinct().Order();
        Assert.Equal(Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)), days);
        Assert.Contains(metrics, m => DateOnly.FromDateTime(m.RecordedAt.UtcDateTime) == today);
    }

    [Fact]
    public async Task Historical_demo_days_do_not_depend_on_the_time_of_day_seeding_runs()
    {
        var early = new DateTimeOffset(2026, 10, 30, 0, 7, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2026, 10, 30, 23, 59, 0, TimeSpan.Zero);
        var today = DateOnly.FromDateTime(early.UtcDateTime);
        var earlyDatabase = "seeder-early-" + Guid.NewGuid();
        var lateDatabase = "seeder-late-" + Guid.NewGuid();

        await SeedAsync(NewPassword(), NewPassword(), now: early, databaseName: earlyDatabase);
        await SeedAsync(NewPassword(), NewPassword(), now: late, databaseName: lateDatabase);

        var earlyHistory = (await DemoMetricsAsync(earlyDatabase))
            .Where(m => DateOnly.FromDateTime(m.RecordedAt.UtcDateTime) < today)
            .Select(Fingerprint)
            .ToList();
        var lateHistory = (await DemoMetricsAsync(lateDatabase))
            .Where(m => DateOnly.FromDateTime(m.RecordedAt.UtcDateTime) < today)
            .Select(Fingerprint)
            .ToList();

        Assert.NotEmpty(earlyHistory);
        Assert.Equal(earlyHistory, lateHistory);
        Assert.All(earlyHistory, h => Assert.InRange(h.RecordedAt.UtcDateTime.Hour, 8, 17));
    }

    [Fact]
    public async Task Demo_metrics_are_deterministic_for_the_same_clock()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var first = "seeder-a-" + Guid.NewGuid();
        var second = "seeder-b-" + Guid.NewGuid();

        await SeedAsync(NewPassword(), NewPassword(), now: now, databaseName: first);
        await SeedAsync(NewPassword(), NewPassword(), now: now, databaseName: second);

        Assert.Equal(
            (await DemoMetricsAsync(first)).Select(Fingerprint),
            (await DemoMetricsAsync(second)).Select(Fingerprint));
    }

    private static (DateTimeOffset RecordedAt, string Outcome, double TotalDurationSeconds) Fingerprint(RagMetricEntry m) =>
        (m.RecordedAt, m.Outcome, m.TotalDurationSeconds);

    private async Task<List<RagMetricEntry>> DemoMetricsAsync(string? databaseName = null)
    {
        await using var provider = BuildProvider(defaultPassword: null, viewerPassword: null, databaseName: databaseName);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        return await db.RagMetricEntries
            .AsNoTracking()
            .Where(m => m.OrganizationId == NorthwindId)
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    private async Task SeedAsync(
        string? defaultPassword,
        string? viewerPassword,
        SeedMode mode = SeedMode.Development,
        DateTimeOffset? now = null,
        string? databaseName = null)
    {
        await using var provider = BuildProvider(defaultPassword, viewerPassword, now, databaseName);
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>().SeedAsync(mode);
    }

    private async Task<bool> AcceptsAsync(string email, string password)
    {
        await using var provider = BuildProvider(defaultPassword: null, viewerPassword: null);
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        return await users.CheckPasswordAsync(user, password);
    }

    private async Task<string?> SecurityStampAsync(string email)
    {
        await using var provider = BuildProvider(defaultPassword: null, viewerPassword: null);
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        return user.SecurityStamp;
    }

    private ServiceProvider BuildProvider(
        string? defaultPassword,
        string? viewerPassword,
        DateTimeOffset? now = null,
        string? databaseName = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OpsFlowDbContext>(options => options.UseInMemoryDatabase(databaseName ?? _databaseName));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<OpsFlowDbContext>();

        services.AddSingleton<IClock>(new FixedClock(now ?? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));
        services.AddSingleton(Options.Create(new SeedOptions
        {
            Enabled = true,
            DefaultPassword = defaultPassword,
            ViewerPassword = viewerPassword,
        }));
        services.AddScoped<DevelopmentDataSeeder>();

        return services.BuildServiceProvider();
    }
}
