namespace OpsFlow.Application.Abstractions;

/// <summary>
/// Provides the identity of the current caller. Read by infrastructure services
/// that need tenant isolation (e.g. metric persistence) without depending on
/// ASP.NET Core HTTP types.
/// </summary>
public interface ICallerContext
{
    Guid? OrganizationId { get; }
}
