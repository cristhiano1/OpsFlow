using System.Globalization;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Api.IntegrationTests.Infrastructure;

namespace OpsFlow.Api.IntegrationTests.Documents;

/// <summary>
/// Proves that the SQL Server 2025 container, Microsoft.Data.SqlClient 6.x,
/// and EF Core 10 natively support the <c>vector</c> data type,
/// <see cref="SqlVector{T}"/>, and <c>VECTOR_DISTANCE</c> end-to-end.
/// Uses a test-only table that is created and dropped within the test class.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class VectorCapabilityTests : IAsyncLifetime
{
    private const string ProbeTableName = "VectorCapabilityProbe";

    private static readonly float[] WriteReadValues = [0.5f, -0.3f, 0.8f];
    private static readonly float[] UnitX = [1f, 0f, 0f];
    private static readonly float[] UnitY = [0f, 1f, 0f];
    private static readonly float[] UnitZ = [0f, 0f, 1f];
    private static readonly float[] Normalized06_08 = [0.6f, 0.8f, 0f];
    private static readonly float[] Equidistant = [0.577f, 0.577f, 0.577f];

    private readonly SqlServerFixture _fixture;
    private VectorProbeDbContext _probeContext = null!;

    public VectorCapabilityTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            $"""
            IF OBJECT_ID('{ProbeTableName}', 'U') IS NOT NULL
                DROP TABLE [{ProbeTableName}];
            CREATE TABLE [{ProbeTableName}] (
                [Id] int NOT NULL PRIMARY KEY,
                [Embedding] vector(3) NOT NULL
            );
            """);

        _probeContext = CreateProbeContext();
    }

    public async Task DisposeAsync()
    {
        await _probeContext.DisposeAsync();

        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            $"IF OBJECT_ID('{ProbeTableName}', 'U') IS NOT NULL DROP TABLE [{ProbeTableName}];");
    }

    // ================================================================
    // Server version
    // ================================================================

    [Fact]
    public async Task Server_reports_major_version_17_or_higher()
    {
        await using var db = _fixture.CreateContext();
        await using var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SERVERPROPERTY('ProductMajorVersion')";
        var result = await cmd.ExecuteScalarAsync();

        var majorVersion = int.Parse(result!.ToString()!, CultureInfo.InvariantCulture);
        Assert.True(majorVersion >= 17,
            $"Expected SQL Server 2025 (major >= 17), got major version {majorVersion}");
    }

    // ================================================================
    // EF native write + read
    // ================================================================

    [Fact]
    public async Task EF_writes_and_reads_SqlVector_natively()
    {
        var original = new SqlVector<float>(WriteReadValues);

        _probeContext.Probes.Add(new VectorProbe { Id = 100, Embedding = original });
        await _probeContext.SaveChangesAsync();
        _probeContext.ChangeTracker.Clear();

        var loaded = await _probeContext.Probes
            .AsNoTracking()
            .FirstAsync(p => p.Id == 100);

        Assert.Equal(3, loaded.Embedding.Length);

        var values = loaded.Embedding.Memory.ToArray();
        Assert.Equal(0.5f, values[0], precision: 5);
        Assert.Equal(-0.3f, values[1], precision: 5);
        Assert.Equal(0.8f, values[2], precision: 5);
    }

    // ================================================================
    // EF VECTOR_DISTANCE — ranking
    // ================================================================

    [Fact]
    public async Task EF_VectorDistance_ranks_nearest_vector_first()
    {
        _probeContext.Probes.Add(new VectorProbe { Id = 201, Embedding = new SqlVector<float>(UnitX) });
        _probeContext.Probes.Add(new VectorProbe { Id = 202, Embedding = new SqlVector<float>(UnitY) });
        await _probeContext.SaveChangesAsync();
        _probeContext.ChangeTracker.Clear();

        var queryVector = new SqlVector<float>(UnitX);

        var ranked = await _probeContext.Probes
            .AsNoTracking()
            .Where(p => p.Id == 201 || p.Id == 202)
            .OrderBy(p => EF.Functions.VectorDistance("cosine", p.Embedding, queryVector))
            .Select(p => new { p.Id, Distance = EF.Functions.VectorDistance("cosine", p.Embedding, queryVector) })
            .ToListAsync();

        Assert.Equal(2, ranked.Count);
        Assert.Equal(201, ranked[0].Id);
        Assert.True(ranked[0].Distance < ranked[1].Distance,
            $"Expected Id 201 closer than Id 202, got distances {ranked[0].Distance} vs {ranked[1].Distance}");
    }

    // ================================================================
    // EF VECTOR_DISTANCE — identical vectors yield ~0
    // ================================================================

    [Fact]
    public async Task EF_VectorDistance_returns_approximately_zero_for_identical_vectors()
    {
        _probeContext.Probes.Add(new VectorProbe { Id = 301, Embedding = new SqlVector<float>(Normalized06_08) });
        await _probeContext.SaveChangesAsync();
        _probeContext.ChangeTracker.Clear();

        var queryVector = new SqlVector<float>(Normalized06_08);

        var distance = await _probeContext.Probes
            .AsNoTracking()
            .Where(p => p.Id == 301)
            .Select(p => EF.Functions.VectorDistance("cosine", p.Embedding, queryVector))
            .FirstAsync();

        Assert.True(double.IsFinite(distance), $"Distance should be finite, got {distance}");
        Assert.InRange(distance, -1e-6, 1e-6);
    }

    // ================================================================
    // EF VECTOR_DISTANCE — result is finite
    // ================================================================

    [Fact]
    public async Task EF_VectorDistance_returns_finite_values()
    {
        _probeContext.Probes.Add(new VectorProbe { Id = 401, Embedding = new SqlVector<float>(UnitX) });
        _probeContext.Probes.Add(new VectorProbe { Id = 402, Embedding = new SqlVector<float>(UnitZ) });
        await _probeContext.SaveChangesAsync();
        _probeContext.ChangeTracker.Clear();

        var queryVector = new SqlVector<float>(Equidistant);

        var distances = await _probeContext.Probes
            .AsNoTracking()
            .Where(p => p.Id == 401 || p.Id == 402)
            .Select(p => EF.Functions.VectorDistance("cosine", p.Embedding, queryVector))
            .ToListAsync();

        Assert.All(distances, d => Assert.True(double.IsFinite(d), $"Distance must be finite, got {d}"));
    }

    // ================================================================
    // Helpers
    // ================================================================

    private VectorProbeDbContext CreateProbeContext()
        => new(new DbContextOptionsBuilder<VectorProbeDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .Options);

    // ================================================================
    // Test-only EF model — not part of the production schema
    // ================================================================

    private sealed class VectorProbe
    {
        public int Id { get; set; }
        public SqlVector<float> Embedding { get; set; }
    }

    private sealed class VectorProbeDbContext : DbContext
    {
        public DbSet<VectorProbe> Probes => Set<VectorProbe>();

        public VectorProbeDbContext(DbContextOptions<VectorProbeDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<VectorProbe>(b =>
            {
                b.ToTable(ProbeTableName);
                b.HasKey(p => p.Id);
                b.Property(p => p.Embedding)
                    .HasColumnType("vector(3)")
                    .IsRequired();
            });
        }
    }
}
