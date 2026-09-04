namespace PiCommandCenter.Application.Validation;

/// <summary>
/// Outcome of a validation pass: either valid, or invalid with a non-empty list of errors.
/// </summary>
public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    private static readonly ValidationResult SuccessResult = new(Array.Empty<string>());

    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Success => SuccessResult;

    public static ValidationResult Failure(params string[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var messages = errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray();
        if (messages.Length == 0)
        {
            throw new ArgumentException("At least one non-empty error message is required.", nameof(errors));
        }

        return new ValidationResult(messages);
    }
}
