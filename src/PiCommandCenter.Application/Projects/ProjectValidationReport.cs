namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Outcome of validating a <see cref="RegisterProjectCommand"/>: either valid or a list of
/// human-readable error messages. No exceptions for validation failures on the validate path.
/// </summary>
public sealed record ProjectValidationReport(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public static ProjectValidationReport Success { get; } = new(true, Array.Empty<string>());

    public static ProjectValidationReport Failure(IReadOnlyList<string> errors) =>
        new(false, errors);
}
