using OpsFlow.Domain.Projects;

namespace OpsFlow.Domain.UnitTests.Projects;

public sealed class ProjectTests
{
    private static readonly Guid ValidId = Guid.NewGuid();
    private static readonly Guid ValidOrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidProjectIsCreatedCorrectly()
    {
        var project = new Project(ValidId, ValidOrgId, "My Project", "Some description", Now);

        Assert.Equal(ValidId, project.Id);
        Assert.Equal(ValidOrgId, project.OrganizationId);
        Assert.Equal("My Project", project.Name);
        Assert.Equal("Some description", project.Description);
        Assert.Equal(Now, project.CreatedAt);
    }

    [Fact]
    public void ValidProjectWithNullDescription()
    {
        var project = new Project(ValidId, ValidOrgId, "My Project", null, Now);

        Assert.Null(project.Description);
    }

    [Fact]
    public void EmptyIdIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new Project(Guid.Empty, ValidOrgId, "Name", null, Now));

        Assert.Equal("id", ex.ParamName);
    }

    [Fact]
    public void EmptyOrganizationIdIsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new Project(ValidId, Guid.Empty, "Name", null, Now));

        Assert.Equal("organizationId", ex.ParamName);
    }

    [Fact]
    public void NullNameIsRejected()
    {
        _ = Assert.ThrowsAny<ArgumentException>(() =>
            new Project(ValidId, ValidOrgId, null!, null, Now));
    }

    [Fact]
    public void EmptyNameIsRejected()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new Project(ValidId, ValidOrgId, "", null, Now));
    }

    [Fact]
    public void WhitespaceNameIsRejected()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new Project(ValidId, ValidOrgId, "   ", null, Now));
    }

    [Fact]
    public void NameAtMaxLengthIsAccepted()
    {
        var name = new string('A', Project.NameMaxLength);
        var project = new Project(ValidId, ValidOrgId, name, null, Now);

        Assert.Equal(name, project.Name);
    }

    [Fact]
    public void NameExceedingMaxLengthIsRejected()
    {
        var name = new string('A', Project.NameMaxLength + 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            new Project(ValidId, ValidOrgId, name, null, Now));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void DescriptionAtMaxLengthIsAccepted()
    {
        var description = new string('B', Project.DescriptionMaxLength);
        var project = new Project(ValidId, ValidOrgId, "Name", description, Now);

        Assert.Equal(description, project.Description);
    }

    [Fact]
    public void DescriptionExceedingMaxLengthIsRejected()
    {
        var description = new string('B', Project.DescriptionMaxLength + 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            new Project(ValidId, ValidOrgId, "Name", description, Now));

        Assert.Equal("description", ex.ParamName);
    }
}
