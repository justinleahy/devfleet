using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure;

/// <summary>Registers the reservation authority.</summary>
public static class ReservationServiceCollectionExtensions
{
    public static IServiceCollection AddReservations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IReservationService, ReservationService>();
        return services;
    }
}
