using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpsFlow.Application.Authorization;
using OpsFlow.Application.Documents;
using OpsFlow.Contracts.Documents;

namespace OpsFlow.Api.Projects;

/// <summary>Handles project-scoped grounded question-answering (RAG) endpoints.</summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/answer")]
[Authorize]
public sealed class ProjectAnswerController : ControllerBase
{
    private const int MaxQuestionLength = 2500;

    // Stable external status strings — decoupled from the Application enum names.
    private const string AnsweredStatus = "answered";
    private const string InsufficientEvidenceStatus = "insufficient_evidence";

    /// <summary>
    /// Answers a natural-language question about the specified project's documents
    /// using grounded retrieval-augmented generation. The answer is supported by
    /// verified citations drawn exclusively from retrieved document chunks.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AnswerAsync(
        Guid projectId,
        [FromBody] AnswerProjectQuestionRequest? request,
        [FromServices] AnswerProjectQuestionService answerService,
        [FromServices] ILogger<ProjectAnswerController> logger,
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

        if (request.Question is null)
        {
            return ValidationProblem(detail: "Question is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return ValidationProblem(detail: "Question must not be empty or whitespace.");
        }

        if (request.Question.Length > MaxQuestionLength)
        {
            return ValidationProblem(
                detail: $"Question length ({request.Question.Length}) exceeds maximum ({MaxQuestionLength}).");
        }

        if (!request.Question.EnumerateRunes().Any(Rune.IsLetterOrDigit))
        {
            return ValidationProblem(detail: "Question must contain at least one letter or digit.");
        }

        AnswerProjectQuestionResult result;
        try
        {
            result = await answerService.AnswerAsync(
                new AnswerProjectQuestionQuery(
                    organizationId,
                    projectId,
                    request.Question),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AnswerGenerationException ex)
        {
            logger.LogError(
                ex,
                "Answer generation provider failure for Project {ProjectId}, Organization {OrganizationId}",
                projectId,
                organizationId);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return new EmptyResult();
        }
        catch (GroundedAnswerValidationException ex)
        {
            logger.LogError(
                ex,
                "Grounded answer contract violation for Project {ProjectId}, Organization {OrganizationId}",
                projectId,
                organizationId);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected answer failure in Project {ProjectId}, Organization {OrganizationId}",
                projectId,
                organizationId);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return new EmptyResult();
        }

        switch (result.Status)
        {
            case AnswerProjectQuestionStatus.ProjectNotFound:
                return NotFound();

            case AnswerProjectQuestionStatus.InsufficientEvidence:
                return Ok(new AnswerProjectQuestionResponse(InsufficientEvidenceStatus, null, []));

            case AnswerProjectQuestionStatus.Success:
                var answer = result.Answer!;
                var citations = answer.Citations.Select(c => new GroundedCitationResponse(
                    c.DocumentId,
                    c.DocumentChunkId,
                    c.ChunkIndex,
                    c.StartOffset,
                    c.EndOffset,
                    c.Text)).ToList();
                return Ok(new AnswerProjectQuestionResponse(AnsweredStatus, answer.Text, citations));

            default:
                logger.LogError(
                    "Unrecognized answer result status {Status} for Project {ProjectId}, Organization {OrganizationId}",
                    result.Status,
                    projectId,
                    organizationId);
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return new EmptyResult();
        }
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
