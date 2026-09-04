using PiCommandCenter.Domain;

namespace PiCommandCenter.Domain.Tests;

public class ProjectIdTests
{
    [Fact]
    public void New_returns_distinct_non_empty_ids()
    {
        var first = ProjectId.New();
        var second = ProjectId.New();

        Assert.NotEqual(first, second);
        Assert.NotEqual(Guid.Empty, first.Value);
        Assert.NotEqual(Guid.Empty, second.Value);
    }

    [Fact]
    public void Empty_guid_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ProjectId(Guid.Empty));
    }

    [Fact]
    public void Equality_follows_the_underlying_value()
    {
        var value = Guid.NewGuid();

        Assert.Equal(new ProjectId(value), new ProjectId(value));
    }

    [Fact]
    public void ToString_matches_the_value()
    {
        var value = Guid.NewGuid();

        Assert.Equal(value.ToString(), new ProjectId(value).ToString());
    }
}
