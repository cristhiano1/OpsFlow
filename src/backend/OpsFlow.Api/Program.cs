using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Infrastructure;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Seeding;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpsFlowInfrastructure(builder.Configuration);

var app = builder.Build();

await ApplyDevelopmentDataAsync(app);

// HTTPS redirection is enforced only outside Development; local development is
// served over HTTP behind the Vite dev proxy (see docs/architecture).
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

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
