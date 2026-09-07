using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class OpenAiGroundedAnswerGeneratorDiTests
{
    private static ServiceProvider BuildProvider(IDictionary<string, string?> configEntries)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        InfrastructureServiceCollectionExtensions.AddAnswerGenerationProvider(services, configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolves_IGroundedAnswerGenerator_when_api_key_is_absent()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var generator = provider.GetRequiredService<IGroundedAnswerGenerator>();

        Assert.NotNull(generator);
        Assert.IsType<Infrastructure.Documents.OpenAiGroundedAnswerGenerator>(generator);
    }

    [Fact]
    public async Task Resolved_generator_without_api_key_throws_on_invoke()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var generator = provider.GetRequiredService<IGroundedAnswerGenerator>();

        var ex = await Assert.ThrowsAsync<AnswerGenerationException>(() =>
            generator.GenerateAsync(
                new GroundedAnswerGenerationRequest("system", "user"), CancellationToken.None));

        Assert.Contains("OpenAI:ApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_is_registered_as_singleton()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var first = provider.GetRequiredService<IGroundedAnswerGenerator>();
        var second = provider.GetRequiredService<IGroundedAnswerGenerator>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Resolves_as_OpenAiGroundedAnswerGenerator_when_api_key_is_present()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "sk-test-key-for-di-only",
        });

        var generator = provider.GetRequiredService<IGroundedAnswerGenerator>();

        Assert.IsType<Infrastructure.Documents.OpenAiGroundedAnswerGenerator>(generator);
    }

    [Fact]
    public void Answer_model_defaults_when_not_configured()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "sk-test-key-for-di-only",
        });

        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Infrastructure.Configuration.OpenAIAnswerGenerationOptions>>();

        Assert.Equal("gpt-4o-mini", options.Value.AnswerModel);
    }

    [Fact]
    public void Answer_model_is_bound_from_configuration()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OpenAI:ApiKey"] = "sk-test-key-for-di-only",
            ["OpenAI:AnswerModel"] = "gpt-4.1-mini",
        });

        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Infrastructure.Configuration.OpenAIAnswerGenerationOptions>>();

        Assert.Equal("gpt-4.1-mini", options.Value.AnswerModel);
    }
}
