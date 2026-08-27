using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Api.Authentication;
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

var app = builder.Build();

await ApplyDevelopmentDataAsync(app);

// HTTPS redirection is enforced only outside Development; local development is
// served over HTTP behind the Vite dev proxy (see docs/architecture).
if (!app.Environment.IsDevelopment())
{
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

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

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

/// <summary>Program entry point marker, exposed for integration testing.</summary>
public partial class Program
{
}
