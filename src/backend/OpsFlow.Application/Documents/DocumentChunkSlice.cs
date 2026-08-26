namespace OpsFlow.Application.Documents;

/// <summary>
/// A chunk boundary produced by <see cref="IDocumentChunker"/>. Offsets are
/// UTF-16 code-unit positions: <see cref="StartOffset"/> inclusive,
/// <see cref="EndOffset"/> exclusive.
/// </summary>
/// <param name="StartOffset">Inclusive UTF-16 code-unit offset.</param>
/// <param name="EndOffset">Exclusive UTF-16 code-unit offset.</param>
public sealed record DocumentChunkSlice(int StartOffset, int EndOffset);
