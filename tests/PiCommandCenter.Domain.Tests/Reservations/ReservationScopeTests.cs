using PiCommandCenter.Domain.Reservations;

namespace PiCommandCenter.Domain.Tests.Reservations;

public class ReservationScopeTests
{
    [Theory]
    [InlineData(ReservationScopeKind.File, "src/a.cs", "src/a.cs")]
    [InlineData(ReservationScopeKind.File, "src\\a.cs", "src/a.cs")]
    [InlineData(ReservationScopeKind.File, "./src//a.cs", "src/a.cs")]
    [InlineData(ReservationScopeKind.Directory, "src/foo", "src/foo/")]
    [InlineData(ReservationScopeKind.Directory, "src/foo/", "src/foo/")]
    [InlineData(ReservationScopeKind.Resource, " project-build ", "project-build")]
    public void Normalization_is_deterministic(
        ReservationScopeKind kind,
        string raw,
        string expected)
    {
        Assert.Equal(expected, ReservationScope.Create(kind, raw).Path);
    }

    [Theory]
    [InlineData(ReservationScopeKind.File, "/abs/path")]
    [InlineData(ReservationScopeKind.File, "C:/temp/x.cs")]
    [InlineData(ReservationScopeKind.File, "~/x.cs")]
    [InlineData(ReservationScopeKind.File, "../outside.cs")]
    [InlineData(ReservationScopeKind.File, "src/../../outside.cs")]
    [InlineData(ReservationScopeKind.File, ".git/config")]
    [InlineData(ReservationScopeKind.Directory, "src/.git/")]
    [InlineData(ReservationScopeKind.File, "")]
    [InlineData(ReservationScopeKind.File, "   ")]
    [InlineData(ReservationScopeKind.Resource, "a/b")]
    [InlineData(ReservationScopeKind.Resource, "..")]
    public void Invalid_scopes_are_rejected(ReservationScopeKind kind, string raw)
    {
        Assert.Throws<InvalidReservationScopeException>(() => ReservationScope.Create(kind, raw));
    }

    // SPEC 17.3 deterministic conflict matrix.
    public static TheoryData<string, string, bool> ConflictMatrix => new()
    {
        // existing, requested, conflict
        { "F:src/Foo.cs", "F:src/Foo.cs", true },
        { "D:src/", "F:src/Foo.cs", true },
        { "D:src/Foo/", "F:src/Foo/Bar.cs", true },
        { "D:src/Foo/", "D:src/Foo/Baz/", true },
        { "D:src/Foo/", "D:src/Foobar/", false },
        { "F:tests/A.cs", "F:src/A.cs", false },
        { "D:src/", "D:src/a/", true },
        { "F:src/A.cs", "D:src/", true },
    };

    [Theory]
    [MemberData(nameof(ConflictMatrix))]
    public void Conflict_matrix_is_symmetric_and_deterministic(
        string existing,
        string requested,
        bool conflict)
    {
        var existingScope = Parse(existing);
        var requestedScope = Parse(requested);

        Assert.Equal(conflict, ReservationScope.ConflictsWith(existingScope, requestedScope));
        Assert.Equal(conflict, ReservationScope.ConflictsWith(requestedScope, existingScope));
    }

    [Fact]
    public void Resource_conflicts_only_with_the_same_resource_name()
    {
        var build = ReservationScope.Create(ReservationScopeKind.Resource, "project-build");
        var buildAgain = ReservationScope.Create(ReservationScopeKind.Resource, "project-build");
        var format = ReservationScope.Create(ReservationScopeKind.Resource, "project-format");
        var file = ReservationScope.Create(ReservationScopeKind.File, "project-build");

        Assert.True(ReservationScope.ConflictsWith(build, buildAgain));
        Assert.False(ReservationScope.ConflictsWith(build, format));
        Assert.False(ReservationScope.ConflictsWith(build, file));
        Assert.False(ReservationScope.ConflictsWith(file, build));
    }

    [Fact]
    public void Coverage_authorizes_exact_files_contained_files_and_resource_names()
    {
        var directory = ReservationScope.Create(ReservationScopeKind.Directory, "src/");
        var file = ReservationScope.Create(ReservationScopeKind.File, "src/a.cs");
        var resource = ReservationScope.Create(ReservationScopeKind.Resource, "project-build");

        Assert.True(file.Covers(ReservationScope.Create(ReservationScopeKind.File, "src/a.cs")));
        Assert.True(directory.Covers(ReservationScope.Create(ReservationScopeKind.File, "src/a.cs")));
        Assert.False(directory.Covers(ReservationScope.Create(ReservationScopeKind.File, "srcx/a.cs")));
        Assert.True(resource.Covers(ReservationScope.Create(ReservationScopeKind.Resource, "project-build")));
        Assert.False(file.Covers(ReservationScope.Create(ReservationScopeKind.Resource, "project-build")));
    }

    private static ReservationScope Parse(string encoded)
    {
        var kind = encoded[0] switch
        {
            'F' => ReservationScopeKind.File,
            'D' => ReservationScopeKind.Directory,
            _ => ReservationScopeKind.Resource,
        };
        return ReservationScope.Create(kind, encoded[2..]);
    }
}
