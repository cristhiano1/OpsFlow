using System.Text;
using OpsFlow.Application.Documents;

namespace OpsFlow.Api.IntegrationTests.Evaluation;

/// <summary>
/// Test-support only. A deterministic <see cref="IChunkReranker"/> that scores
/// each candidate by simple lexical overlap between the query and the candidate
/// text. It exists solely to exercise the reranking orchestration, validation,
/// ordering, tenant-safety, and comparison plumbing end to end.
///
/// <para>
/// This is NOT a production cross-encoder and does NOT prove real-world
/// reranking quality. It uses only legitimate runtime inputs — the query and
/// each candidate's text — and never touches evaluation data (relevance grades,
/// case ids, expected chunk keys, or metrics). The score is a transparent,
/// auditable overlap fraction; it is not tuned against any gold labels.
/// </para>
/// </summary>
internal sealed class DeterministicTokenOverlapReranker : IChunkReranker
{
    public RerankerIdentity Identity { get; } = new(
        "opsflow-test-token-overlap-v1",
        "deterministic-token-overlap",
        MaxCandidates: 50);

    public Task<IReadOnlyList<ChunkRerankScore>> RerankAsync(
        ChunkRerankRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queryTokens = Tokenize(request.Query);

        IReadOnlyList<ChunkRerankScore> scores =
            [.. request.Candidates.Select(candidate =>
                new ChunkRerankScore(candidate.DocumentChunkId, Score(queryTokens, candidate.Text)))];

        return Task.FromResult(scores);
    }

    private static double Score(HashSet<string> queryTokens, string candidateText)
    {
        if (queryTokens.Count == 0)
        {
            return 0.0;
        }

        var candidateTokens = Tokenize(candidateText);

        int matched = 0;
        foreach (var token in queryTokens)
        {
            if (candidateTokens.Contains(token))
            {
                matched++;
            }
        }

        // Overlap fraction of the query's unique tokens found in the candidate.
        return matched / (double)queryTokens.Count;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder();

        foreach (var rune in text.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString());
            }
            else if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }
}
