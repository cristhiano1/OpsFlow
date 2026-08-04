namespace OpsFlow.Application.UnitTests.TestSupport;

/// <summary>
/// A marker exception used to verify that infrastructure failures propagate
/// unchanged through LoginService instead of being converted into a login
/// failure result.
/// </summary>
internal sealed class FakeInfrastructureException(string message) : Exception(message);
