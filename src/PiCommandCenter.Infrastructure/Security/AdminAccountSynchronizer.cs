using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Ensures the single local administrator exists with the hash from the private password file.
/// </summary>
public sealed class AdminAccountSynchronizer(
    UserManager<IdentityUser> userManager,
    IOptions<AdminOptions> adminOptions,
    IHostEnvironment environment,
    ILogger<AdminAccountSynchronizer> logger)
{
    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var admin = adminOptions.Value;
        var hash = AuthMaterialLoader.LoadPasswordHash(admin, environment);
        var username = admin.Username.Trim();

        var existing = await userManager.FindByNameAsync(username).ConfigureAwait(false);
        if (existing is null)
        {
            var user = new IdentityUser
            {
                UserName = username,
                NormalizedUserName = userManager.NormalizeName(username),
                SecurityStamp = Guid.NewGuid().ToString("N"),
            };

            var create = await userManager.CreateAsync(user).ConfigureAwait(false);
            if (!create.Succeeded)
            {
                throw new InvalidOperationException(
                    "Failed to create the local administrator: " + string.Join("; ", create.Errors.Select(e => e.Description)));
            }

            user.PasswordHash = hash;
            var hashed = await userManager.UpdateAsync(user).ConfigureAwait(false);
            if (!hashed.Succeeded)
            {
                throw new InvalidOperationException(
                    "Failed to store the local administrator hash: " + string.Join("; ", hashed.Errors.Select(e => e.Description)));
            }

            logger.LogInformation("Local administrator account synchronized from password file.");
            return;
        }

        if (!string.Equals(existing.PasswordHash, hash, StringComparison.Ordinal))
        {
            existing.PasswordHash = hash;
            var update = await userManager.UpdateAsync(existing).ConfigureAwait(false);
            if (!update.Succeeded)
            {
                throw new InvalidOperationException(
                    "Failed to update the local administrator hash: " + string.Join("; ", update.Errors.Select(e => e.Description)));
            }
        }

        logger.LogInformation("Local administrator account is present.");
    }
}
