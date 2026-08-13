using Microsoft.Extensions.Options;
using OpsFlow.Application.Documents;
using OpsFlow.Infrastructure.Configuration;
using OpsFlow.Infrastructure.Documents;

namespace OpsFlow.Infrastructure.UnitTests.Documents;

public sealed class LocalDocumentStorageTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly LocalDocumentStorage _storage;

    public LocalDocumentStorageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "opsflow-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        var options = Options.Create(new DocumentStorageOptions { BasePath = _tempRoot });
        _storage = new LocalDocumentStorage(options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static DocumentStorageAddress MakeAddress(
        Guid? orgId = null, Guid? projectId = null, Guid? docId = null) =>
        new(
            orgId ?? Guid.NewGuid(),
            projectId ?? Guid.NewGuid(),
            docId ?? Guid.NewGuid());

    private string ExpectedPath(DocumentStorageAddress addr) =>
        Path.Combine(
            _tempRoot,
            addr.OrganizationId.ToString("N"),
            addr.ProjectId.ToString("N"),
            addr.DocumentId.ToString("N"));

    // ================================================================
    // Save + round-trip
    // ================================================================

    [Fact]
    public async Task Saved_bytes_are_readable_at_expected_path()
    {
        var addr = MakeAddress();
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        using var input = new MemoryStream(data);

        await _storage.SaveAsync(addr, input, CancellationToken.None);

        var path = ExpectedPath(addr);
        Assert.True(File.Exists(path));
        var stored = await File.ReadAllBytesAsync(path);
        Assert.Equal(data, stored);
    }

    [Fact]
    public async Task Directories_created_from_trusted_ids()
    {
        var addr = MakeAddress();
        using var input = new MemoryStream([0xFF]);

        await _storage.SaveAsync(addr, input, CancellationToken.None);

        var orgDir = Path.Combine(_tempRoot, addr.OrganizationId.ToString("N"));
        var projDir = Path.Combine(orgDir, addr.ProjectId.ToString("N"));
        Assert.True(Directory.Exists(orgDir));
        Assert.True(Directory.Exists(projDir));
    }

    [Fact]
    public async Task Final_object_name_derived_from_document_id()
    {
        var addr = MakeAddress();
        using var input = new MemoryStream([0xAB]);

        await _storage.SaveAsync(addr, input, CancellationToken.None);

        var path = ExpectedPath(addr);
        Assert.Equal(addr.DocumentId.ToString("N"), Path.GetFileName(path));
    }

    [Fact]
    public async Task Configured_root_is_respected()
    {
        var addr = MakeAddress();
        using var input = new MemoryStream([0x01]);

        await _storage.SaveAsync(addr, input, CancellationToken.None);

        var path = ExpectedPath(addr);
        Assert.StartsWith(_tempRoot, path, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // Overwrite prevention
    // ================================================================

    [Fact]
    public async Task Save_to_existing_key_throws()
    {
        var addr = MakeAddress();
        using var first = new MemoryStream([0x01]);
        await _storage.SaveAsync(addr, first, CancellationToken.None);

        using var second = new MemoryStream([0x02]);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.SaveAsync(addr, second, CancellationToken.None));
    }

    // ================================================================
    // Delete behavior
    // ================================================================

    [Fact]
    public async Task Delete_removes_existing_object()
    {
        var addr = MakeAddress();
        using var input = new MemoryStream([0x01]);
        await _storage.SaveAsync(addr, input, CancellationToken.None);

        var path = ExpectedPath(addr);
        Assert.True(File.Exists(path));

        await _storage.DeleteAsync(addr, CancellationToken.None);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Delete_absent_object_is_idempotent()
    {
        var addr = MakeAddress();
        await _storage.DeleteAsync(addr, CancellationToken.None);
    }

    // ================================================================
    // Cancellation
    // ================================================================

    [Fact]
    public async Task Cancellation_during_copy_leaves_no_final_object()
    {
        var addr = MakeAddress();
        using var cts = new CancellationTokenSource();
        var slowStream = new CancellingStream(cts);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _storage.SaveAsync(addr, slowStream, cts.Token));

        var path = ExpectedPath(addr);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Failed_copy_leaves_no_temp_artifact()
    {
        var addr = MakeAddress();
        var failingStream = new FailingStream();

        _ = await Assert.ThrowsAsync<IOException>(() =>
            _storage.SaveAsync(addr, failingStream, CancellationToken.None));

        var dir = Path.GetDirectoryName(ExpectedPath(addr))!;
        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(dir);
            Assert.Empty(files);
        }
    }

    [Fact]
    public async Task Successful_copy_leaves_no_temp_artifact()
    {
        var addr = MakeAddress();
        using var input = new MemoryStream([0x01, 0x02]);
        await _storage.SaveAsync(addr, input, CancellationToken.None);

        var dir = Path.GetDirectoryName(ExpectedPath(addr))!;
        var files = Directory.GetFiles(dir);
        _ = Assert.Single(files);
        Assert.Equal(ExpectedPath(addr), files[0]);
    }

    // ================================================================
    // Address validation
    // ================================================================

    [Fact]
    public void Address_with_empty_org_id_is_rejected()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new DocumentStorageAddress(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Address_with_empty_project_id_is_rejected()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new DocumentStorageAddress(Guid.NewGuid(), Guid.Empty, Guid.NewGuid()));
    }

    [Fact]
    public void Address_with_empty_document_id_is_rejected()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new DocumentStorageAddress(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty));
    }

    // ================================================================
    // OpenReadAsync — round-trip
    // ================================================================

    [Fact]
    public async Task OpenRead_after_save_returns_exact_bytes()
    {
        var addr = MakeAddress();
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        using (var input = new MemoryStream(data))
        {
            await _storage.SaveAsync(addr, input, CancellationToken.None);
        }

        var stream = await _storage.OpenReadAsync(addr, CancellationToken.None);
        Assert.NotNull(stream);
        using (stream)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(data, ms.ToArray());
        }
    }

    [Fact]
    public async Task OpenRead_nonexistent_file_returns_null()
    {
        var addr = MakeAddress();
        var result = await _storage.OpenReadAsync(addr, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task OpenRead_missing_parent_directory_returns_null()
    {
        var addr = new DocumentStorageAddress(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await _storage.OpenReadAsync(addr, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task OpenRead_returned_stream_is_readable()
    {
        var addr = MakeAddress();
        using (var input = new MemoryStream([0xAB]))
        {
            await _storage.SaveAsync(addr, input, CancellationToken.None);
        }

        var stream = await _storage.OpenReadAsync(addr, CancellationToken.None);
        Assert.NotNull(stream);
        using (stream)
        {
            Assert.True(stream.CanRead);
        }
    }

    [Fact]
    public async Task OpenRead_caller_can_dispose_returned_stream()
    {
        var addr = MakeAddress();
        using (var input = new MemoryStream([0x01]))
        {
            await _storage.SaveAsync(addr, input, CancellationToken.None);
        }

        var stream = await _storage.OpenReadAsync(addr, CancellationToken.None);
        Assert.NotNull(stream);
        stream.Dispose();
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task OpenRead_resolved_address_uses_guid_only()
    {
        var addr = MakeAddress();
        using (var input = new MemoryStream([0xFF]))
        {
            await _storage.SaveAsync(addr, input, CancellationToken.None);
        }

        var stream = await _storage.OpenReadAsync(addr, CancellationToken.None);
        Assert.NotNull(stream);
        using (stream)
        {
            var path = ExpectedPath(addr);
            Assert.Equal(addr.DocumentId.ToString("N"), Path.GetFileName(path));
        }
    }

    [Fact]
    public async Task OpenRead_cancellation_already_requested_throws()
    {
        var addr = MakeAddress();
        using (var input = new MemoryStream([0x01]))
        {
            await _storage.SaveAsync(addr, input, CancellationToken.None);
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _storage.OpenReadAsync(addr, cts.Token));
    }

    // ================================================================
    // Configuration validation
    // ================================================================

    [Fact]
    public void Blank_base_path_throws()
    {
        var options = Options.Create(new DocumentStorageOptions { BasePath = "" });
        _ = Assert.Throws<InvalidOperationException>(() =>
            new LocalDocumentStorage(options));
    }

    // ================================================================
    // Test helpers
    // ================================================================

    private sealed class CancellingStream : Stream
    {
        private readonly CancellationTokenSource _cts;
        public CancellingStream(CancellationTokenSource cts) => _cts = cts;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Simulated read failure");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => throw new IOException("Simulated read failure");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => throw new IOException("Simulated read failure");
    }
}
