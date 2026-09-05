using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Infrastructure.Mail;

namespace PiCommandCenter.Infrastructure;

/// <summary>Registers the mail coordination services (SPEC §16).</summary>
public static class MailServiceCollectionExtensions
{
    public static IServiceCollection AddAgentMail(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IMessageService, MailService>();
        services.AddScoped<IAgentIdentityRegistry, AgentIdentityRegistry>();
        return services;
    }
}
