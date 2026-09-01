using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    public EmbeddingGeneratorIdentity Identity { get; set; } =
        new(EmbeddingProfiles.SemanticV1Id, EmbeddingProfiles.SemanticV1ModelId, EmbeddingProfiles.SemanticV1Dimensions);

    public IReadOnlyList<ReadOnlyMemory<float>>? GenerateResult { get; set; }
    public bool GenerateCalled { get; private set; }
    public IReadOnlyList<string>? ReceivedTexts { get; private set; }

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        GenerateCalled = true;
        ReceivedTexts = texts;

        if (GenerateResult is not null)
        {
            return Task.FromResult(GenerateResult);
        }

        IReadOnlyList<ReadOnlyMemory<float>> defaultResult =
            [.. texts.Select(_ => (ReadOnlyMemory<float>)new float[Identity.Dimensions])];

        return Task.FromResult(defaultResult);
    }
}
