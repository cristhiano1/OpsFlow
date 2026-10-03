using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Api.Authentication;
using OpsFlow.Api.Cors;
using OpsFlow.Api.HealthChecks;
using OpsFlow.Api.RateLimiting;
using OpsFlow.Application.Authentication;
using OpsFlow.Application.Documents;
using OpsFlow.Application.Projects;
using OpsFlow.Infrastructure;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Documents;
using OpsFlow.Infrastructure.Projects;
using OpsFlow.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpsFlowInfrastructure(builder.Configuration);
builder.Services.AddOpsFlowAuthentication();
builder.Services.AddOpsFlowCors(builder.Configuration);
builder.Services.AddOpsFlowRateLimiting(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<SqlServerHealthCheck>("sqlserver", tags: ["ready"]);
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<RefreshService>();
builder.Services.AddScoped<LogoutService>();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddScoped<IProjectRepository, EfProjectRepository>();
builder.Services.AddScoped<CreateProjectService>();
builder.Services.AddScoped<ListProjectsService>();
builder.Services.AddScoped<IDocumentRepository, EfDocumentRepository>();
builder.Services.AddScoped<ListDocumentsService>();
builder.Services.AddOptions<DocumentStorageOptions>()
    .Bind(builder.Configuration.GetSection(DocumentStorageOptions.SectionName))
    .PostConfigure<IHostEnvironment>((opts, env) =>
    {
        opts.BasePath = DocumentStorageOptions.ResolveBasePath(opts.BasePath, env.ContentRootPath);
    });
builder.Services.AddSingleton<IDocumentStorage, LocalDocumentStorage>();
builder.Services.AddScoped<UploadDocumentService>();
builder.Services.AddScoped<IngestDocumentService>();
builder.Services.AddScoped<GetDocumentContentService>();
builder.Services.AddScoped<IDocumentExtractionRepository, EfDocumentExtractionRepository>();
builder.Services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
builder.Services.AddSingleton<IDocumentTextExtractor, DocxTextExtractor>();
builder.Services.AddScoped<ExtractDocumentTextService>();
builder.Services.AddScoped<GetDocumentExtractionService>();
builder.Services.AddSingleton<IDocumentChunker, DeterministicDocumentChunker>();
builder.Services.AddScoped<IDocumentChunkSetRepository, EfDocumentChunkSetRepository>();
builder.Services.AddScoped<EnsureDocumentChunksService>();
builder.Services.AddScoped<IDocumentChunkSnapshotReader, EfDocumentChunkSnapshotReader>();
builder.Services.AddScoped<IDocumentEmbeddingSetRepository, EfDocumentEmbeddingSetRepository>();
builder.Services.AddScoped<EnsureDocumentEmbeddingsService>();
builder.Services.AddScoped<ISemanticChunkRetriever, EfSemanticChunkRetriever>();
builder.Services.AddScoped<SearchDocumentChunksService>();
builder.Services.AddScoped<ILexicalChunkRetriever, EfLexicalChunkRetriever>();
builder.Services.AddScoped<SearchDocumentChunksLexicallyService>();
builder.Services.AddScoped<SearchDocumentChunksHybridService>();
builder.Services.AddScoped<SearchDocumentChunksRerankedService>();

// Map the non-secret configuration flag onto the provider-neutral Application
// policy. Default OFF: reranking is not activated in the grounded-answer path
// until deliberately enabled (see ADR-011). The hybrid /search endpoint and the
// standalone reranked service are unaffected by this flag.
//
// The flag is read when the scoped service is resolved, not captured eagerly at
// startup, so the effective configuration is honored — including host
// configuration sources applied after these top-level statements run (e.g. a
// test host overriding the flag). The mapping stays in the API composition root;
// the Application layer receives only the enum and never depends on IConfiguration.
builder.Services.AddScoped(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var answerRetrievalPolicy =
        configuration.GetValue<bool>("Reranking:ActivateInAnswerPath")
            ? AnswerRetrievalPolicy.RerankWithHybridFallback
            : AnswerRetrievalPolicy.HybridOnly;

    // The reranked search is passed as a factory, not resolved here, so the
    // reranked/provider (Bedrock reranker + AWS client) graph is constructed only
    // when the reranking path runs. Under HybridOnly it is never resolved.
    return new AnswerProjectQuestionService(
        serviceProvider.GetRequiredService<SearchDocumentChunksHybridService>(),
        () => serviceProvider.GetRequiredService<SearchDocumentChunksRerankedService>(),
        serviceProvider.GetRequiredService<IGroundedAnswerGenerator>(),
        answerRetrievalPolicy,
        serviceProvider.GetRequiredService<IGroundedAnswerTelemetry>());
});

if (!builder.Environment.IsDevelopment())
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        var caddyNetwork = builder.Configuration["ForwardedHeaders:TrustedNetwork"];
        if (!string.IsNullOrWhiteSpace(caddyNetwork))
        {
            var parts = caddyNetwork.Split('/');
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                IPAddress.Parse(parts[0]),
                int.Parse(parts[1], CultureInfo.InvariantCulture)));
        }
    });
}

var app = builder.Build();

await ApplyMigrationsIfRequestedAsync(app);
await ApplyDevelopmentDataAsync(app);
await ApplyDemoDataIfRequestedAsync(app);

if (!app.Environment.IsDevelopment())
{
    app.UseForwardedHeaders();
    app.UseExceptionHandler(exceptionApp =>
    {
        exceptionApp.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
    });
    app.UseHttpsRedirection();
}

app.UseCors(CorsFlowExtensions.PolicyName);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

app.MapControllers();

app.Run();

// Applies EF Core migrations when APPLY_MIGRATIONS=true. This is separate from
// development seeding: it runs MigrateAsync only, with no seed data, so it is safe
// for production-like container startup. Defaults to off; docker-compose.yml sets it.
static async Task ApplyMigrationsIfRequestedAsync(WebApplication app)
{
    var apply = app.Configuration.GetValue<bool>("APPLY_MIGRATIONS");
    if (!apply)
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var database = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
    await database.Database.MigrateAsync();
}

// Applies EF Core migrations and development seed data. Runs only when
// (Development AND Seed:Enabled) OR the environment is Testing. Never in Production.
static async Task ApplyDevelopmentDataAsync(WebApplication app)
{
    var environment = app.Environment;
    var seedOptions = app.Services.GetRequiredService<IOptions<SeedOptions>>().Value;

    var shouldSeed = (environment.IsDevelopment() && seedOptions.Enabled)
        || environment.IsEnvironment("Testing");

    if (!shouldSeed)
    {
        return;
    }

    using var scope = app.Services.CreateScope();

    var database = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
    await database.Database.MigrateAsync();

    var seeder = scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>();
    await seeder.SeedAsync();
}

// Seeds demo data in Production when SEED_DEMO_DATA=true. This is a separate
// path from development seeding: it reuses the same DevelopmentDataSeeder (which
// is idempotent) but is gated on an explicit environment variable so it never
// runs unless deliberately enabled for a public demo instance.
static async Task ApplyDemoDataIfRequestedAsync(WebApplication app)
{
    var shouldSeed = app.Configuration.GetValue<bool>("SEED_DEMO_DATA");
    if (!shouldSeed)
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>();
    await seeder.SeedAsync();
    app.Logger.LogInformation("Demo data seeded (SEED_DEMO_DATA=true)");
}

/// <summary>Program entry point marker, exposed for integration testing.</summary>
public partial class Program
{
}
