using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Authorization;
using OpsFlow.Application.Documents;
using OpsFlow.Contracts.Documents;

namespace OpsFlow.Api.Projects;

/// <summary>Handles project-scoped search endpoints.</summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/search")]
[Authorize]
public sealed class ProjectSearchController : ControllerBase
{
    private const int DefaultTopK = 10;
    private const int MaxQueryTextLength = 2500;
    private const int MinTopK = 1;
    private const int MaxTopK = 50;

    /// <summary>
    /// Searches for document chunks within the specified project using hybrid
    /// semantic and lexical retrieval.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SearchAsync(
        Guid projectId,
        [FromBody] SearchDocumentChunksRequest? request,
        [FromServices] SearchDocumentChunksHybridService searchService,
        [FromServices] ILogger<ProjectSearchController> logger,
        CancellationToken cancellationToken)
    {
        if (!TryGetOrganizationId(out var organizationId))
        {
            return UnauthorizedWithoutBody();
        }

        if (request is null)
        {
            return ValidationProblem();
        }

        if (request.QueryText is null)
        {
            return ValidationProblem(detail: "Query text is required.");
        }

        if (string.IsNullOrWhiteSpace(request.QueryText))
        {
            return ValidationProblem(detail: "Query text must not be empty or whitespace.");
        }

        if (request.QueryText.Length > MaxQueryTextLength)
        {
            return ValidationProblem(
                detail: $"Query text length ({request.QueryText.Length}) exceeds maximum ({MaxQueryTextLength}).");
        }

        if (!request.QueryText.EnumerateRunes().Any(Rune.IsLetterOrDigit))
        {
            return ValidationProblem(detail: "Query text must contain at least one letter or digit.");
        }

        var topK = request.TopK ?? DefaultTopK;

        if (topK is < MinTopK or > MaxTopK)
        {
            return ValidationProblem(
                detail: $"TopK must be between {MinTopK} and {MaxTopK}, but was {topK}.");
        }

        SearchDocumentChunksHybridResult result;
        try
        {
            result = await searchService.SearchAsync(
                new SearchDocumentChunksHybridQuery(
                    organizationId,
                    projectId,
                    request.QueryText,
                    topK),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EmbeddingGenerationException ex)
        {
            logger.LogError(
                ex,
                "Embedding provider failure for search in Project {ProjectId}, Organization {OrganizationId}",
                projectId,
                organizationId);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected search failure in Project {ProjectId}, Organization {OrganizationId}",
                projectId,
                organizationId);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return new EmptyResult();
        }

        if (!result.ProjectFound)
        {
            return NotFound();
        }

        var items = result.Hits.Select(h => new SearchDocumentChunkHitResponse(
            h.DocumentId,
            h.DocumentChunkId,
            h.ChunkIndex,
            h.StartOffset,
            h.EndOffset,
            h.Text)).ToList();

        return Ok(new SearchDocumentChunksResponse(items));
    }

    private bool TryGetOrganizationId(out Guid organizationId)
    {
        organizationId = Guid.Empty;
        var claim = User.FindFirst(OpsFlowClaimTypes.OrganizationId)?.Value;
        if (string.IsNullOrWhiteSpace(claim))
        {
            return false;
        }

        if (!Guid.TryParse(claim, out organizationId) || organizationId == Guid.Empty)
        {
            return false;
        }

        return true;
    }

    private EmptyResult UnauthorizedWithoutBody()
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return new EmptyResult();
    }
}
