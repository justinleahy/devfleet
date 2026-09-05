namespace PiCommandCenter.Domain.Mail;

/// <summary>
/// Delivery priority of an agent mail message (SPEC §16.4). High marks messages that
/// deserve attention-inbox treatment, including human guidance.
/// </summary>
public enum MessageImportance
{
    Normal = 0,
    High = 1,
}
