namespace PiCommandCenter.Application.VerificationPolicy;

/// <summary>Raised when the designated node rejects a verification-policy selection.</summary>
public sealed class VerificationPolicySelectionException : InvalidOperationException
{
    public VerificationPolicySelectionException(string message)
        : base(message)
    {
    }
}
