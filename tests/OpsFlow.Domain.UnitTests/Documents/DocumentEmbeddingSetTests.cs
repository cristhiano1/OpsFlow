using OpsFlow.Domain.Documents;

namespace OpsFlow.Domain.UnitTests.Documents;

public sealed class DocumentEmbeddingSetTests
{
    private static readonly Guid ValidId = Guid.NewGuid();
    private static readonly Guid ValidDocumentId = Guid.NewGuid();
    private static readonly DateTimeOffset ValidTimestamp = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_with_valid_arguments_succeeds()
    {
        var set = new DocumentEmbeddingSet(
            ValidId, ValidDocumentId, 1, "opsflow-semantic-v1",
            "text-embedding-3-small", 1536, 5, ValidTimestamp);

        Assert.Equal(ValidId, set.Id);
        Assert.Equal(ValidDocumentId, set.DocumentId);
        Assert.Equal(1, set.ChunkingVersion);
        Assert.Equal("opsflow-semantic-v1", set.ProfileId);
        Assert.Equal("text-embedding-3-small", set.ModelId);
        Assert.Equal(1536, set.Dimensions);
        Assert.Equal(5, set.EmbeddingCount);
        Assert.Equal(ValidTimestamp, set.CreatedAt);
    }

    [Fact]
    public void Constructor_rejects_empty_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentEmbeddingSet(
                Guid.Empty, ValidDocumentId, 1, "profile",
                "model", 1536, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_rejects_empty_document_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentEmbeddingSet(
                ValidId, Guid.Empty, 1, "profile",
                "model", 1536, 0, ValidTimestamp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_invalid_chunking_version(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentEmbeddingSet(
                ValidId, ValidDocumentId, version, "profile",
                "model", 1536, 0, ValidTimestamp));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_null_empty_whitespace_profile_id(string? profileId)
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentEmbeddingSet(
                ValidId, ValidDocumentId, 1, profileId!,
                "model", 1536, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_rejects_profile_id_exceeding_max_length()
    {
        var longProfileId = new string('p', DocumentEmbeddingSet.MaxProfileIdLength + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentEmbeddingSet(
                ValidId, ValidDocumentId, 1, longProfileId,
                "model", 1536, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_accepts_profile_id_at_max_length()
    {
        var profileId = new string('p', DocumentEmbeddingSet.MaxProfileIdLength);

        var set = new DocumentEmbeddingSet(
            ValidId, ValidDocumentId, 1, profileId,
            "model", 1536, 0, ValidTimestamp);

        Assert.Equal(DocumentEmbeddingSet.MaxProfileIdLength, set.ProfileId.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_null_empty_whitespace_model_id(string? modelId)
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentEmbeddingSet(
                ValidId, ValidDocumentId, 1, "profile",
                modelId!, 1536, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_rejects_model_id_exceeding_max_length()
    {
        var longModelId = new string('m', DocumentEmbeddingSet.MaxModelIdLength + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentEmbeddingSet(
                ValidId, ValidDocumentId, 1, "profile",
                longModelId, 1536, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_accepts_model_id_at_max_length()
    {
        var modelId = new string('m', DocumentEmbeddingSet.MaxModelIdLength);

        var set = new DocumentEmbeddingSet(
            ValidId, ValidDocumentId, 1, "profile",
            modelId, 1536, 0, ValidTimestamp);

        Assert.Equal(DocumentEmbeddingSet.MaxModelIdLength, set.ModelId.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_invalid_dimensions(int dimensions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentEmbeddingSet(
                ValidId, ValidDocumentId, 1, "profile",
                "model", dimensions, 0, ValidTimestamp));
    }

    [Fact]
    public void Constructor_rejects_negative_embedding_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DocumentEmbeddingSet(
                ValidId, ValidDocumentId, 1, "profile",
                "model", 1536, -1, ValidTimestamp));
    }

    [Fact]
    public void Constructor_accepts_zero_embedding_count()
    {
        var set = new DocumentEmbeddingSet(
            ValidId, ValidDocumentId, 1, "profile",
            "model", 1536, 0, ValidTimestamp);

        Assert.Equal(0, set.EmbeddingCount);
    }

    [Fact]
    public void MaxProfileIdLength_is_100()
    {
        Assert.Equal(100, DocumentEmbeddingSet.MaxProfileIdLength);
    }

    [Fact]
    public void MaxModelIdLength_is_200()
    {
        Assert.Equal(200, DocumentEmbeddingSet.MaxModelIdLength);
    }
}
