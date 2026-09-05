namespace PiCommandCenter.Node.Tests;

public sealed class MuseCodeOptionsValidatorTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var result = new MuseCodeOptionsValidator().Validate(MuseCodeOptions.SectionName, new MuseCodeOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Fact]
    public void Smallest_positive_bounds_are_accepted()
    {
        var options = new MuseCodeOptions
        {
            Executable = "muse",
            StartTimeoutSeconds = 1,
            RequestTimeoutSeconds = 1,
            CancelGraceSeconds = 1,
            MaxStderrLines = 1,
            MaxLineBytes = 1,
        };

        var result = new MuseCodeOptionsValidator().Validate(MuseCodeOptions.SectionName, options);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Fact]
    public void Blank_executable_and_non_positive_bounds_each_fail_by_name()
    {
        var options = new MuseCodeOptions
        {
            Executable = "   ",
            StartTimeoutSeconds = 0,
            RequestTimeoutSeconds = 0,
            CancelGraceSeconds = -1,
            MaxStderrLines = 0,
            MaxLineBytes = -5,
        };

        var result = new MuseCodeOptionsValidator().Validate(MuseCodeOptions.SectionName, options);

        Assert.True(result.Failed);
        var failures = (result.Failures ?? []).ToArray();
        Assert.Equal(6, failures.Length);
        foreach (var name in new[]
                 {
                     nameof(MuseCodeOptions.Executable),
                     nameof(MuseCodeOptions.StartTimeoutSeconds),
                     nameof(MuseCodeOptions.RequestTimeoutSeconds),
                     nameof(MuseCodeOptions.CancelGraceSeconds),
                     nameof(MuseCodeOptions.MaxStderrLines),
                     nameof(MuseCodeOptions.MaxLineBytes),
                 })
        {
            Assert.Contains(failures, failure => failure.Contains($"'{name}'", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(nameof(MuseCodeOptions.StartTimeoutSeconds))]
    [InlineData(nameof(MuseCodeOptions.RequestTimeoutSeconds))]
    [InlineData(nameof(MuseCodeOptions.CancelGraceSeconds))]
    [InlineData(nameof(MuseCodeOptions.MaxStderrLines))]
    [InlineData(nameof(MuseCodeOptions.MaxLineBytes))]
    public void A_single_zero_bound_fails_only_that_bound(string property)
    {
        var options = new MuseCodeOptions();
        typeof(MuseCodeOptions).GetProperty(property)!.SetValue(options, 0);

        var result = new MuseCodeOptionsValidator().Validate(MuseCodeOptions.SectionName, options);

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures ?? []);
        Assert.Contains($"'{property}'", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_muse_section_and_unnamed_options_are_validated()
    {
        var invalid = new MuseCodeOptions { Executable = string.Empty };
        var validator = new MuseCodeOptionsValidator();

        Assert.True(validator.Validate("Antigravity", invalid).Skipped);
        Assert.True(validator.Validate(MuseCodeOptions.SectionName, invalid).Failed);
        Assert.True(validator.Validate(null, invalid).Failed);
        Assert.True(validator.Validate(string.Empty, invalid).Failed);
    }
}
