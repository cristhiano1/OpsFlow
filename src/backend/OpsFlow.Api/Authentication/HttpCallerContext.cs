using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Authorization;

namespace OpsFlow.Api.Authentication;

/// <summary>
/// Reads the caller's identity from the current HTTP request's claims.
/// </summary>
internal sealed class HttpCallerContext(IHttpContextAccessor httpContextAccessor) : ICallerContext
{
    /// <inheritdoc />
    public Guid? OrganizationId
    {
        get
        {
            var claim = httpContextAccessor.HttpContext?.User
                .FindFirst(OpsFlowClaimTypes.OrganizationId)?.Value;
            return claim is not null && Guid.TryParse(claim, out var id) ? id : null;
        }
    }
}
