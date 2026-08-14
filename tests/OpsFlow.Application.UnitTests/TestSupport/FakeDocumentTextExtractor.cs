using OpsFlow.Application.Documents;

namespace OpsFlow.Application.UnitTests.TestSupport;

internal sealed class FakeDocumentTextExtractor : IDocumentTextExtractor
{
    public string SupportedContentType { get; set; } = "application/pdf";

    public DocumentTextExtractionResult? ExtractResult { get; set; }
    public bool ExtractCalled { get; private set; }
    public int? LastMaxCharacters { get; private set; }

    public bool CanExtract(string canonicalContentType) =>
        string.Equals(canonicalContentType, SupportedContentType, StringComparison.OrdinalIgnoreCase);

    public Task<DocumentTextExtractionResult> ExtractAsync(
        Stream content,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        ExtractCalled = true;
        LastMaxCharacters = maxCharacters;
        return Task.FromResult(
            ExtractResult ?? DocumentTextExtractionResult.Success(string.Empty));
    }
}
