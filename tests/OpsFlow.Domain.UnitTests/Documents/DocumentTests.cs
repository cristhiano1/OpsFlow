using OpsFlow.Domain.Documents;

namespace OpsFlow.Domain.UnitTests.Documents;

public sealed class DocumentTests
{
    private static readonly Guid ValidId = Guid.NewGuid();
    private static readonly Guid ValidOrgId = Guid.NewGuid();
    private static readonly Guid ValidProjectId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidDocumentPreservesAllValues()
    {
        var doc = new Document(ValidId, ValidOrgId, ValidProjectId, "report.pdf", "application/pdf", 4096, Now);

        Assert.Equal(ValidId, doc.Id);
        Assert.Equal(ValidOrgId, doc.OrganizationId);
        Assert.Equal(ValidProjectId, doc.ProjectId);
        Assert.Equal("report.pdf", doc.OriginalFileName);
        Assert.Equal("application/pdf", doc.ContentType);
        Assert.Equal(4096, doc.SizeBytes);
        Assert.Equal(Now, doc.CreatedAt);
    }

    [Fact]
    public void ZeroSizeBytesIsAccepted()
    {
        var doc = new Document(ValidId, ValidOrgId, ValidProjectId, "empty.txt", "text/plain", 0, Now);

        Assert.Equal(0, doc.SizeBytes);
    }

    [Fact]
    public void EmptyIdIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new Document(Guid.Empty, ValidOrgId, ValidProjectId, "f.pdf", "application/pdf", 1, Now));

        Assert.Equal("id", ex.ParamName);
    }

    [Fact]
    public void EmptyOrganizationIdIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new Document(ValidId, Guid.Empty, ValidProjectId, "f.pdf", "application/pdf", 1, Now));

        Assert.Equal("organizationId", ex.ParamName);
    }

    [Fact]
    public void EmptyProjectIdIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new Document(ValidId, ValidOrgId, Guid.Empty, "f.pdf", "application/pdf", 1, Now));

        Assert.Equal("projectId", ex.ParamName);
    }

    [Fact]
    public void NullOriginalFileNameIsRejected()
    {
        _ = Assert.ThrowsAny<ArgumentException>(() =>
            new Document(ValidId, ValidOrgId, ValidProjectId, null!, "application/pdf", 1, Now));
    }

    [Fact]
    public void WhitespaceOriginalFileNameIsRejected()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new Document(ValidId, ValidOrgId, ValidProjectId, "   ", "application/pdf", 1, Now));
    }

    [Fact]
    public void OriginalFileNameAtMaxLengthIsAccepted()
    {
        var name = new string('a', Document.OriginalFileNameMaxLength);
        var doc = new Document(ValidId, ValidOrgId, ValidProjectId, name, "application/pdf", 1, Now);

        Assert.Equal(name, doc.OriginalFileName);
    }

    [Fact]
    public void OriginalFileNameExceedingMaxLengthIsRejected()
    {
        var name = new string('a', Document.OriginalFileNameMaxLength + 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            new Document(ValidId, ValidOrgId, ValidProjectId, name, "application/pdf", 1, Now));

        Assert.Equal("originalFileName", ex.ParamName);
    }

    [Fact]
    public void NullContentTypeIsRejected()
    {
        _ = Assert.ThrowsAny<ArgumentException>(() =>
            new Document(ValidId, ValidOrgId, ValidProjectId, "f.pdf", null!, 1, Now));
    }

    [Fact]
    public void WhitespaceContentTypeIsRejected()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new Document(ValidId, ValidOrgId, ValidProjectId, "f.pdf", "   ", 1, Now));
    }

    [Fact]
    public void ContentTypeAtMaxLengthIsAccepted()
    {
        var ct = new string('x', Document.ContentTypeMaxLength);
        var doc = new Document(ValidId, ValidOrgId, ValidProjectId, "f.pdf", ct, 1, Now);

        Assert.Equal(ct, doc.ContentType);
    }

    [Fact]
    public void ContentTypeExceedingMaxLengthIsRejected()
    {
        var ct = new string('x', Document.ContentTypeMaxLength + 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            new Document(ValidId, ValidOrgId, ValidProjectId, "f.pdf", ct, 1, Now));

        Assert.Equal("contentType", ex.ParamName);
    }

    [Fact]
    public void NegativeSizeBytesIsRejected()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Document(ValidId, ValidOrgId, ValidProjectId, "f.pdf", "application/pdf", -1, Now));
    }
}
