namespace PiCommandCenter.Application.Requests;

/// <summary>Raised when a node no longer owns a renewable request claim.</summary>
public sealed class ClaimRenewalRejectedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
