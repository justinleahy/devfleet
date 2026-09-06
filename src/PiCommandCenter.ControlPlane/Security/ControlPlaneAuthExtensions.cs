using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Security;

namespace PiCommandCenter.ControlPlane.Security;

/// <summary>Registers Identity cookies, node token authentication, and authorization policies.</summary>
public static class ControlPlaneAuthExtensions
{
    public static IServiceCollection AddControlPlaneAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.Configure<AdminOptions>(configuration.GetSection(AdminOptions.SectionName));
        services.Configure<NodeAuthenticationOptions>(configuration.GetSection(NodeAuthenticationOptions.SectionName));

        var nodeAuthentication = new NodeAuthenticationOptions();
        configuration.GetSection(NodeAuthenticationOptions.SectionName).Bind(nodeAuthentication);
        services.AddSingleton(new NodeCredentialRegistry(nodeAuthentication, environment));

        var keysPath = configuration["DataProtection:KeysDirectory"];
        ArgumentException.ThrowIfNullOrWhiteSpace(keysPath);
        var keysDirectory = Path.GetFullPath(PrivateFileAccess.ExpandPath(keysPath));
        PrivateFileAccess.CreatePrivateDirectory(keysDirectory);
        services.AddDataProtection()
            .SetApplicationName("PiCommandCenter.ControlPlane")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        services.AddIdentityCore<IdentityUser>(options =>
            {
                options.User.RequireUniqueEmail = false;
                options.Password.RequiredLength = 12;
                options.Lockout.MaxFailedAccessAttempts = 10;
            })
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddEntityFrameworkStores<ControlPlaneDbContext>();

        var authentication = services.AddAuthentication(IdentityConstants.ApplicationScheme);
        authentication.AddIdentityCookies(cookies =>
        {
            cookies.ApplicationCookie!.Configure(cookie =>
            {
                cookie.LoginPath = "/login";
                cookie.AccessDeniedPath = "/access-denied";
                cookie.ReturnUrlParameter = "returnUrl";
                cookie.Cookie.Name = "pcc.admin";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.SlidingExpiration = true;
            });
        });
        authentication.AddScheme<AuthenticationSchemeOptions, NodeTokenAuthenticationHandler>(
            NodeTokenDefaults.AuthenticationScheme,
            _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Admin, policy =>
            {
                policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme);
                policy.RequireAuthenticatedUser();
            });
            options.AddPolicy(AuthPolicies.Node, policy =>
            {
                policy.AddAuthenticationSchemes(NodeTokenDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
            });
            options.FallbackPolicy = new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser()
                .Build();
        });

        services.AddHttpContextAccessor();
        services.AddScoped<AdminAccountSynchronizer>();
        services.AddCascadingAuthenticationState();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "RequestVerificationToken";
            options.Cookie.Name = "pcc.af";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });

        return services;
    }

    /// <summary>Applies deterministic file-backed auth settings used by test hosts. Production is unchanged.</summary>
    public static void UseTestAuthFiles(
        this IWebHostBuilder builder,
        string passwordFile,
        string credentialDirectory,
        string username = "admin",
        string? dataProtectionKeysDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var passwordDirectory = Path.GetDirectoryName(Path.GetFullPath(passwordFile))
            ?? throw new ArgumentException("Password file must have a parent directory.", nameof(passwordFile));
        builder.UseEnvironment("Testing");
        builder.UseSetting("Admin:Username", username);
        builder.UseSetting("Admin:PasswordFile", passwordFile);
        builder.UseSetting("NodeAuthentication:CredentialDirectory", credentialDirectory);
        builder.UseSetting("NodeAuthentication:Header", NodeAuthenticationOptions.DefaultHeader);
        builder.UseSetting("NodeAuthentication:Scheme", NodeAuthenticationOptions.DefaultScheme);
        builder.UseSetting(
            "DataProtection:KeysDirectory",
            dataProtectionKeysDirectory ?? Path.Combine(passwordDirectory, "data-protection-keys"));
    }
}
