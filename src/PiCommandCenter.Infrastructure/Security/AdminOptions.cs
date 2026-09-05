namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Local administrator identity. Bound from the <c>Admin</c> configuration section.
/// The password itself is never stored in configuration; only a private hash file path is.
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Single local administrator user name.</summary>
    public string Username { get; set; } = "admin";

    /// <summary>Path to a 0600 file containing the ASP.NET Identity password hash.</summary>
    public string PasswordFile { get; set; } = "~/.config/pi-command-center/admin.password.hash";
}
