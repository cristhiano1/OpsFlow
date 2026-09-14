namespace OpsFlow.Application.Documents;

/// <summary>
/// Reranked retrieval use case. Composes the unchanged
/// <see cref="SearchDocumentChunksHybridService"/> with a provider-neutral
/// <see cref="IChunkReranker"/>: it retrieves a bounded candidate pool via
/// hybrid RRF, asks the reranker to score those candidates, validates the
/// scores as untrusted input, and applies deterministic authoritative ordering.
/// All chunk metadata comes from the retrieved <see cref="HybridChunkHit"/>;
/// the reranker only supplies relevance scores. The hybrid baseline remains
/// independently executable — this service does not modify it.
///
/// <para>
/// This foundation is fail-closed: if reranking fails, the call fails rather
/// than silently returning the hybrid ordering. A fail-open policy, if adopted,
/// belongs to a later RAG-activation decision.
/// </para>
/// </summary>
public sealed class SearchDocumentChunksRerankedService
{
    private const int MinTopK = 1;
    private const int MaxTopK = 50;

    /// <summary>Default reranker candidate depth — an unevaluated default (see ADR-009).</summary>
    private const int DefaultCandidateDepth = 20;

    /// <summary>Upper bound on candidate depth, matching the hybrid service's maximum TopK.</summary>
    private const int MaxCandidateDepth = 50;

    private readonly SearchDocumentChunksHybridService _hybridSearch;
    private readonly IChunkReranker _reranker;

    /// <summary>Creates the service with its dependencies.</summary>
    public SearchDocumentChunksRerankedService(
        SearchDocumentChunksHybridService hybridSearch,
        IChunkReranker reranker)
    {
        ArgumentNullException.ThrowIfNull(hybridSearch);
        ArgumentNullException.ThrowIfNull(reranker);

        _hybridSearch = hybridSearch;
        _reranker = reranker;
    }

    /// <summary>
    /// Searches for document chunks using hybrid retrieval followed by reranking.
    /// </summary>
    public async Task<SearchDocumentChunksRerankedResult> SearchAsync(
        SearchDocumentChunksRerankedQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(query));
        }

        if (query.TopK is < MinTopK or > MaxTopK)
        {
            throw new ArgumentOutOfRangeException(nameof(query),
                $"TopK must be between {MinTopK} and {MaxTopK}, but was {query.TopK}.");
        }

        ValidateRerankerIdentity();

        int candidateDepth = Math.Min(MaxCandidateDepth, Math.Max(DefaultCandidateDepth, query.TopK));

        // Hybrid retrieval runs exactly once. The original query text is
        // forwarded unchanged; only the requested count differs (candidate depth,
        // not the caller's TopK). Query-text validation, organization scoping,
        // and the cross-tenant-indistinguishable ProjectNotFound semantics are
        // reused from the hybrid service.
        var hybridResult = await _hybridSearch.SearchAsync(
            new SearchDocumentChunksHybridQuery(
                query.OrganizationId,
                query.ProjectId,
                query.QueryText,
                candidateDepth),
            cancellationToken);

        if (!hybridResult.ProjectFound)
        {
            return SearchDocumentChunksRerankedResult.ProjectNotFound();
        }

        var hybridHits = hybridResult.Hits;
        if (hybridHits.Count == 0)
        {
            // No candidates: never invoke the reranker.
            return SearchDocumentChunksRerankedResult.Success([]);
        }

        var hitByChunkId = BuildHitIndex(hybridHits);

        if (hybridHits.Count > _reranker.Identity.MaxCandidates)
        {
            throw new ChunkRerankingException(
                $"Candidate count {hybridHits.Count} exceeds the reranker's maximum of " +
                $"{_reranker.Identity.MaxCandidates}.");
        }

        var candidates = BuildCandidates(hybridHits);

        var scores = await InvokeRerankerAsync(query.QueryText, candidates, cancellationToken);

        var scoreByChunkId = ValidateScores(scores, hitByChunkId);

        var ordered = OrderReranked(hybridHits, scoreByChunkId, query.TopK);

        return SearchDocumentChunksRerankedResult.Success(ordered);
    }

    private void ValidateRerankerIdentity()
    {
        var identity = _reranker.Identity;

        if (string.IsNullOrWhiteSpace(identity.ProfileId))
        {
            throw new InvalidOperationException("Reranker identity has a missing or blank profile id.");
        }

        if (string.IsNullOrWhiteSpace(identity.ModelId))
        {
            throw new InvalidOperationException("Reranker identity has a missing or blank model id.");
        }

        if (identity.MaxCandidates <= 0)
        {
            throw new InvalidOperationException(
                $"Reranker identity declares MaxCandidates {identity.MaxCandidates}; it must be > 0.");
        }
    }

    private static Dictionary<Guid, HybridChunkHit> BuildHitIndex(IReadOnlyList<HybridChunkHit> hybridHits)
    {
        var index = new Dictionary<Guid, HybridChunkHit>(hybridHits.Count);
        foreach (var hit in hybridHits)
        {
            if (!index.TryAdd(hit.DocumentChunkId, hit))
            {
                // Hybrid retrieval guarantees deduplication by DocumentChunkId; a
                // duplicate here is an upstream invariant violation. Never send
                // ambiguous identity to an external scorer.
                throw new InvalidOperationException(
                    $"Duplicate DocumentChunkId '{hit.DocumentChunkId}' in hybrid result; " +
                    "reranking identity would be ambiguous.");
            }
        }

        return index;
    }

    private static List<ChunkRerankCandidate> BuildCandidates(IReadOnlyList<HybridChunkHit> hybridHits)
    {
        var candidates = new List<ChunkRerankCandidate>(hybridHits.Count);
        for (int i = 0; i < hybridHits.Count; i++)
        {
            var hit = hybridHits[i];
            candidates.Add(new ChunkRerankCandidate(hit.DocumentChunkId, i + 1, hit.Text));
        }

        return candidates;
    }

    private async Task<IReadOnlyList<ChunkRerankScore>> InvokeRerankerAsync(
        string query,
        IReadOnlyList<ChunkRerankCandidate> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _reranker.RerankAsync(new ChunkRerankRequest(query, candidates), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only caller-requested cancellation propagates unchanged. A
            // provider-local timeout that surfaces as OperationCanceledException
            // (or TaskCanceledException) while the caller's token is NOT
            // cancelled is a reranker failure: it falls through to the generic
            // handler below and is wrapped as a ChunkRerankingException.
            throw;
        }
        catch (ChunkRerankingException)
        {
            throw;
        }
        catch (ChunkRerankingValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ChunkRerankingException("The reranker failed to score the candidates.", ex);
        }
    }

    private static Dictionary<Guid, double> ValidateScores(
        IReadOnlyList<ChunkRerankScore> scores,
        Dictionary<Guid, HybridChunkHit> hitByChunkId)
    {
        if (scores is null)
        {
            throw new ChunkRerankingValidationException("Reranker returned a null score collection.");
        }

        if (scores.Count != hitByChunkId.Count)
        {
            throw new ChunkRerankingValidationException(
                $"Reranker returned {scores.Count} scores but {hitByChunkId.Count} candidates were supplied.");
        }

        var scoreByChunkId = new Dictionary<Guid, double>(scores.Count);
        foreach (var score in scores)
        {
            if (score is null)
            {
                // A well-formed collection can still contain a null element from a
                // malformed provider response or deserialization. Reject it before
                // any dereference; never skip, repair, or reduce the count.
                throw new ChunkRerankingValidationException("Reranker returned a null score entry.");
            }

            if (!hitByChunkId.ContainsKey(score.DocumentChunkId))
            {
                throw new ChunkRerankingValidationException(
                    $"Reranker returned a score for unknown candidate '{score.DocumentChunkId}'.");
            }

            if (!double.IsFinite(score.RelevanceScore))
            {
                throw new ChunkRerankingValidationException(
                    $"Reranker returned a non-finite score for candidate '{score.DocumentChunkId}'.");
            }

            if (!scoreByChunkId.TryAdd(score.DocumentChunkId, score.RelevanceScore))
            {
                throw new ChunkRerankingValidationException(
                    $"Reranker returned a duplicate score for candidate '{score.DocumentChunkId}'.");
            }
        }

        // Equal counts + all-known + no-duplicates ⇒ every candidate scored
        // exactly once. Verify explicitly rather than assume.
        foreach (var chunkId in hitByChunkId.Keys)
        {
            if (!scoreByChunkId.ContainsKey(chunkId))
            {
                throw new ChunkRerankingValidationException(
                    $"Reranker did not return a score for candidate '{chunkId}'.");
            }
        }

        return scoreByChunkId;
    }

    private static List<RerankedChunkHit> OrderReranked(
        IReadOnlyList<HybridChunkHit> hybridHits,
        Dictionary<Guid, double> scoreByChunkId,
        int topK)
    {
        // Ordering is decided by the reranker score only; the original hybrid
        // rank and then DocumentChunkId are deterministic tie-breaks. Provider
        // response order is never used. Metadata is projected from the original
        // HybridChunkHit — the reranker cannot influence it.
        var ordered = hybridHits
            .Select((hit, index) => (Hit: hit, OriginalRank: index + 1, Score: scoreByChunkId[hit.DocumentChunkId]))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.OriginalRank)
            .ThenBy(entry => entry.Hit.DocumentChunkId)
            .Take(topK)
            .Select(entry => new RerankedChunkHit(
                entry.Hit.DocumentId,
                entry.Hit.DocumentChunkId,
                entry.Hit.ChunkIndex,
                entry.Hit.StartOffset,
                entry.Hit.EndOffset,
                entry.Hit.Text,
                entry.Score,
                entry.OriginalRank))
            .ToList();

        return ordered;
    }
}
