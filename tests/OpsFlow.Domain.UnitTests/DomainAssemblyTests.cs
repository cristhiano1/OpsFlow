namespace OpsFlow.Domain.UnitTests;

public sealed class DomainAssemblyTests
{
    [Fact]
    public void DomainAssemblyHasExpectedName()
    {
        var assemblyName = typeof(AssemblyMarker).Assembly.GetName().Name;

        Assert.Equal("OpsFlow.Domain", assemblyName);
    }
}
