using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Infrastructure.Completion;
using PiCommandCenter.Infrastructure.Verification;

namespace PiCommandCenter.Infrastructure;

public static class CompletionServiceCollectionExtensions
{
    public static IServiceCollection AddVerificationAndCompletion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IVerificationRunStore, VerificationRunStore>();
        services.AddScoped<IAssignmentTerminalizationService, AssignmentTerminalizationService>();
        return services;
    }
}
