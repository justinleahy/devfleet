using PiCommandCenter.Domain.Reservations;

namespace PiCommandCenter.Domain.Tests.Reservations;

/// <summary>
/// SPEC §17.11 path normalization gaps not covered by <see cref="ReservationScopeTests"/>:
/// separators, absolute/UNC rejection, <c>..</c> traversal, <c>.git</c> (any case),
/// length bound, and filesystem case preservation (no case-folding conflicts).
/// </summary>
public class PathNormalizationMatrixTests
{
    [Theory]
    [InlineData("src\\A.cs", "src/A.cs")]
    [InlineData("src/Mixed\\Sep\\File.cs", "src/Mixed/Sep/File.cs")]
    [InlineData("././src//Foo.CS", "src/Foo.CS")]
    [InlineData("Docs/README.md", "Docs/README.md")]
    public void Separators_collapse_to_posix_and_case_is_preserved(string raw, string expected)
    {
        Assert.Equal(expected, ReservationScope.Create(ReservationScopeKind.File, raw).Path);
    }

    [Fact]
    public void Directory_normalization_keeps_a_trailing_slash_and_original_case()
    {
        var scope = ReservationScope.Create(ReservationScopeKind.Directory, "src\\FooBar");
        Assert.Equal("src/FooBar/", scope.Path);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("//server/share/file.cs")]
    [InlineData("\\\\server\\share\\file.cs")]
    [InlineData("D:\\repo\\src\\A.cs")]
    [InlineData("~/secrets")]
    [InlineData("../escape.cs")]
    [InlineData("src/../escape.cs")]
    [InlineData("src/foo/../../outside.cs")]
    [InlineData("src\\foo\\..\\bar.cs")]
    [InlineData(".git/config")]
    [InlineData(".GIT/HEAD")]
    [InlineData("src/.Git/objects")]
    [InlineData("./")]
    [InlineData(".")]
    public void Traversal_absolute_unc_and_git_paths_are_rejected(string raw)
    {
        Assert.Throws<InvalidReservationScopeException>(
            () => ReservationScope.Create(ReservationScopeKind.File, raw));
    }

    [Fact]
    public void Null_path_is_rejected()
    {
        Assert.Throws<InvalidReservationScopeException>(
            () => ReservationScope.Create(ReservationScopeKind.File, null!));
    }

    [Fact]
    public void Paths_over_the_length_bound_are_rejected()
    {
        var tooLong = new string('a', ReservationScope.MaxPathLength + 1);
        Assert.Throws<InvalidReservationScopeException>(
            () => ReservationScope.Create(ReservationScopeKind.File, tooLong));
    }

    [Fact]
    public void Distinct_case_variants_do_not_conflict()
    {
        var upper = ReservationScope.Create(ReservationScopeKind.File, "src/Foo.cs");
        var lower = ReservationScope.Create(ReservationScopeKind.File, "src/foo.cs");

        Assert.Equal("src/Foo.cs", upper.Path);
        Assert.Equal("src/foo.cs", lower.Path);
        Assert.False(ReservationScope.ConflictsWith(upper, lower));
        Assert.False(upper.Covers(lower));
    }

    [Fact]
    public void Directory_prefix_does_not_match_a_sibling_with_a_shared_prefix()
    {
        var directory = ReservationScope.Create(ReservationScopeKind.Directory, "src/Foo");
        var sibling = ReservationScope.Create(ReservationScopeKind.File, "src/Foobar.cs");
        var nested = ReservationScope.Create(ReservationScopeKind.File, "src/Foo/Bar.cs");

        Assert.False(ReservationScope.ConflictsWith(directory, sibling));
        Assert.True(ReservationScope.ConflictsWith(directory, nested));
        Assert.True(directory.Covers(nested));
        Assert.False(directory.Covers(sibling));
    }
}
