using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class BedrockRerankerDiTests
{
    private static ServiceCollection BuildServices(IDictionary<string, string?> configEntries)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        InfrastructureServiceCollectionExtensions.AddRerankingProvider(services, configuration);
        return services;
    }

    private static BedrockRerankerOptions ResolveOptions(IDictionary<string, string?> configEntries)
    {
        using var provider = BuildServices(configEntries).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<BedrockRerankerOptions>>().Value;
    }

    // Registration is proven via service descriptors rather than by resolving the
    // reranker: constructing the production adapter builds a real AWS client,
    // which eagerly resolves credentials from the AWS chain and would throw in a
    // credential-free CI environment. Identity/behaviour is covered by the pure
    // unit tests over the invoker seam.

    [Fact]
    public void Registers_IChunkReranker_as_singleton_BedrockChunkReranker()
    {
        var services = BuildServices(new Dictionary<string, string?>());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IChunkReranker));
        Assert.Equal(typeof(BedrockChunkReranker), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void Registers_invoker_seam_as_singleton()
    {
        var services = BuildServices(new Dictionary<string, string?>());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IBedrockRerankInvoker));
        Assert.Equal(typeof(BedrockRerankInvoker), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void Registers_options_validator()
    {
        var services = BuildServices(new Dictionary<string, string?>());

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IValidateOptions<BedrockRerankerOptions>)
            && d.ImplementationType == typeof(BedrockRerankerOptionsValidator));
    }

    [Fact]
    public void Does_not_register_reranked_search_service()
    {
        // PR #29 must not activate reranking anywhere; the reranked search use
        // case remains unregistered (deferred to a later activation PR).
        var services = BuildServices(new Dictionary<string, string?>());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(SearchDocumentChunksRerankedService));
    }

    [Fact]
    public void Defaults_bind_when_no_configuration_present()
    {
        var options = ResolveOptions(new Dictionary<string, string?>());

        Assert.Equal("eu-central-1", options.Region);
        Assert.Equal("cohere.rerank-v3-5:0", options.ModelId);
        Assert.Equal(30, options.TimeoutSeconds);
    }

    [Fact]
    public void Configuration_binds_over_defaults()
    {
        var options = ResolveOptions(new Dictionary<string, string?>
        {
            ["BedrockReranker:Region"] = "us-east-1",
            ["BedrockReranker:ModelId"] = "amazon.rerank-v1:0",
            ["BedrockReranker:TimeoutSeconds"] = "45",
        });

        Assert.Equal("us-east-1", options.Region);
        Assert.Equal("amazon.rerank-v1:0", options.ModelId);
        Assert.Equal(45, options.TimeoutSeconds);
    }

    [Fact]
    public void Blank_region_fails_validation()
    {
        Assert.Throws<OptionsValidationException>(() =>
            ResolveOptions(new Dictionary<string, string?> { ["BedrockReranker:Region"] = "" }));
    }

    [Fact]
    public void Blank_model_id_fails_validation()
    {
        Assert.Throws<OptionsValidationException>(() =>
            ResolveOptions(new Dictionary<string, string?> { ["BedrockReranker:ModelId"] = "" }));
    }

    [Fact]
    public void Non_positive_timeout_fails_validation()
    {
        Assert.Throws<OptionsValidationException>(() =>
            ResolveOptions(new Dictionary<string, string?> { ["BedrockReranker:TimeoutSeconds"] = "0" }));
    }

    [Fact]
    public void Options_expose_no_credential_fields()
    {
        var forbidden = new[] { "AccessKeyId", "SecretAccessKey", "SessionToken", "ApiKey" };

        var propertyNames = typeof(BedrockRerankerOptions)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, propertyNames, StringComparer.OrdinalIgnoreCase);
        }
    }
}
