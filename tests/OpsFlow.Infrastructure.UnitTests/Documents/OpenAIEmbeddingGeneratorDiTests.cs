using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class OpenAIEmbeddingGeneratorDiTests
{
    private static ServiceProvider BuildProvider(IDictionary<string, string?> configEntries)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        InfrastructureServiceCollectionExtensions.AddEmbeddingProvider(services, configuration);
        return services.BuildServiceProvider();
    }

    // ================================================================
    // Key absent → the container still resolves the port, and the caller
    // observes a provider-neutral EmbeddingGenerationException on invoke.
    // ================================================================

    [Fact]
    public void Resolves_IEmbeddingGenerator_when_api_key_is_absent()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var generator = provider.GetRequiredService<IEmbeddingGenerator>();

        Assert.NotNull(generator);
        Assert.IsType<Infrastructure.Documents.OpenAIEmbeddingGenerator>(generator);
        Assert.Equal(EmbeddingProfiles.SemanticV1Id, generator.Identity.ProfileId);
    }

    [Fact]
    public async Task Resolved_generator_without_api_key_throws_on_invoke()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var generator = provider.GetRequiredService<IEmbeddingGenerator>();

        var ex = await Assert.ThrowsAsync<EmbeddingGenerationException>(() =>
            generator.GenerateAsync(["hello"], CancellationToken.None));

        Assert.Contains("OpenAI:ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_is_registered_as_singleton()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var first = provider.GetRequiredService<IEmbeddingGenerator>();
        var second = provider.GetRequiredService<IEmbeddingGenerator>();

        Assert.Same(first, second);
    }

    // ================================================================
    // Key present → the container resolves as OpenAIEmbeddingGenerator.
    // (We do not invoke it here — that would require a real HTTP transport,
    // which the transport-level tests already cover.)
    // ================================================================

    [Fact]
    public void Resolves_as_OpenAIEmbeddingGenerator_when_api_key_is_present()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "sk-test-key-for-di-only",
        });

        var generator = provider.GetRequiredService<IEmbeddingGenerator>();

        Assert.IsType<Infrastructure.Documents.OpenAIEmbeddingGenerator>(generator);
    }
}
