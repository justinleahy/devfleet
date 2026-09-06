using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PiCommandCenter.Api;

/// <summary>
/// Host-facing registration for the HTTP API. The legacy <c>/api</c> surface keeps the host's
/// cookie and antiforgery behavior and stays out of OpenAPI; the native <c>/api/v1</c> surface
/// accepts Identity bearer tokens only and is the documented contract.
/// </summary>
public static class PiCommandCenterApiExtensions
{
    private const string LegacyPrefix = "/api";
    private const string NativePrefix = "/api/v1";
    private const string NativeRelativePathPrefix = "api/v1/";
    private const string OpenApiDocumentName = "v1";
    private const string BearerSecuritySchemeName = "Bearer";

    private static readonly BadRequest<ProblemDetails> AntiforgeryRejected = TypedResults.BadRequest(ApiProblems.Problem(
        StatusCodes.Status400BadRequest,
        "Antiforgery validation failed",
        "Unsafe legacy API requests require a valid RequestVerificationToken header."));

    /// <summary>
    /// Registers the Identity opaque bearer scheme, the bearer-only <see cref="ApiAuthorizationPolicies.NativeApi"/>
    /// policy, ProblemDetails, and the <c>v1</c> OpenAPI document. Expects Identity core and the host's
    /// default authentication scheme to be registered separately.
    /// </summary>
    public static IServiceCollection AddPiCommandCenterApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication()
            .AddBearerToken(IdentityConstants.BearerScheme, options =>
            {
                options.BearerTokenExpiration = TimeSpan.FromHours(1);
                options.RefreshTokenExpiration = TimeSpan.FromDays(14);
            });

        services.AddAuthorization(options =>
            options.AddPolicy(ApiAuthorizationPolicies.NativeApi, policy =>
            {
                policy.AddAuthenticationSchemes(IdentityConstants.BearerScheme);
                policy.RequireAuthenticatedUser();
            }));

        services.AddProblemDetails();

        services.AddOpenApi(OpenApiDocumentName, options =>
        {
            options.ShouldInclude = description =>
                description.RelativePath is { } path
                && path.StartsWith(NativeRelativePathPrefix, StringComparison.OrdinalIgnoreCase);
            options.AddDocumentTransformer(DeclareBearerSecuritySchemeAsync);
            options.AddOperationTransformer(RequireBearerAsync);
        });

        return services;
    }

    /// <summary>
    /// Maps <c>/openapi/v1.json</c>, the legacy <c>/api</c> group, and the native <c>/api/v1</c> group.
    /// Legacy routes inherit the host's fallback policy unchanged and reject unsafe requests whose
    /// antiforgery token is missing or invalid; native routes require
    /// <see cref="ApiAuthorizationPolicies.NativeApi"/> and never validate antiforgery tokens.
    /// </summary>
    public static IEndpointRouteBuilder MapPiCommandCenterApi(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapOpenApi().AllowAnonymous();

        var legacy = routes.MapGroup(LegacyPrefix)
            .ExcludeFromDescription()
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AddEndpointFilter(RejectInvalidAntiforgeryAsync);
        MapResourceEndpoints(legacy, LegacyPrefix);

        var native = routes.MapGroup(NativePrefix)
            .RequireAuthorization(ApiAuthorizationPolicies.NativeApi)
            .DisableAntiforgery();
        native.MapNativeAuthEndpoints();
        MapResourceEndpoints(native, NativePrefix);

        return routes;
    }

    private static void MapResourceEndpoints(RouteGroupBuilder group, string locationPrefix)
    {
        group.MapProjectsEndpoints(locationPrefix);
        group.MapRequestsEndpoints(locationPrefix);
        group.MapRequestResultEndpoints(locationPrefix);
        group.MapMailEndpoints(locationPrefix);
        group.MapReservationsEndpoints(locationPrefix);
        group.MapProjectRecoveryEndpoints(locationPrefix);
    }

    /// <summary>
    /// The antiforgery middleware only records validation failures; minimal endpoints without form
    /// parameters never read that feature, so the legacy group turns a failure into 400 before the handler runs.
    /// </summary>
    private static ValueTask<object?> RejectInvalidAntiforgeryAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        return context.HttpContext.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false }
            ? ValueTask.FromResult<object?>(AntiforgeryRejected)
            : next(context);
    }

    private static Task DeclareBearerSecuritySchemeAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerSecuritySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "Opaque access token issued by POST /api/v1/auth/login or /refresh.",
        };
        return Task.CompletedTask;
    }

    private static Task RequireBearerAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var anonymous = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IAllowAnonymous>()
            .Any();
        if (anonymous)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(BearerSecuritySchemeName, context.Document)] = [],
        });
        return Task.CompletedTask;
    }
}
