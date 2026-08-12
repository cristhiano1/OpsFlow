using OpsFlow.Infrastructure.Configuration;

namespace OpsFlow.Infrastructure.UnitTests.Configuration;

public sealed class DocumentStorageOptionsTests
{
    [Fact]
    public void Relative_path_is_resolved_against_content_root()
    {
        var contentRoot = Path.GetTempPath();
        var result = DocumentStorageOptions.ResolveBasePath("storage/files", contentRoot);
        Assert.Equal(Path.GetFullPath("storage/files", contentRoot), result);
    }

    [Fact]
    public void Absolute_path_is_returned_unchanged()
    {
        var absolutePath = Path.GetTempPath();
        var result = DocumentStorageOptions.ResolveBasePath(absolutePath, @"C:\irrelevant");
        Assert.Equal(absolutePath, result);
    }

    [Fact]
    public void Empty_path_is_returned_as_is()
    {
        var result = DocumentStorageOptions.ResolveBasePath("", @"C:\app");
        Assert.Equal("", result);
    }

    [Fact]
    public void Whitespace_path_is_returned_as_is()
    {
        var result = DocumentStorageOptions.ResolveBasePath("   ", @"C:\app");
        Assert.Equal("   ", result);
    }
}
