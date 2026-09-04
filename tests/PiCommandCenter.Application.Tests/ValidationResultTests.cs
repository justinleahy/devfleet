using PiCommandCenter.Application.Validation;

namespace PiCommandCenter.Application.Tests;

public class ValidationResultTests
{
    [Fact]
    public void Success_is_valid_with_no_errors()
    {
        Assert.True(ValidationResult.Success.IsValid);
        Assert.Empty(ValidationResult.Success.Errors);
    }

    [Fact]
    public void Failure_reports_errors_and_is_invalid()
    {
        var result = ValidationResult.Failure("Name is required", "Owner is required");

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("Name is required", result.Errors);
    }

    [Fact]
    public void Failure_requires_at_least_one_message()
    {
        Assert.Throws<ArgumentException>(() => ValidationResult.Failure("   "));
        Assert.Throws<ArgumentException>(() => ValidationResult.Failure());
    }

    [Fact]
    public void Failure_ignores_blank_messages()
    {
        var result = ValidationResult.Failure("", "real error");

        Assert.Single(result.Errors, "real error");
    }
}
