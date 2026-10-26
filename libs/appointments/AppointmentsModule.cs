using Clinic.Shared;
using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Appointments;

public static class AppointmentsModule
{
    public static IServiceCollection AddAppointmentsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Clinic") ?? throw new InvalidOperationException("Connection string 'Clinic' is not configured.");
        services.AddDbContext<AppointmentsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", AppointmentsDbContext.SchemaName)));
        services.Configure<AppointmentOptions>(config.GetSection("Appointments"));
        services.AddScoped<AppointmentService>();
        services.AddScoped<AppointmentsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<AppointmentsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<AppointmentsDbContext>>("appointments-db", tags: ["ready"]);

        messaging.AddOutbox<AppointmentsDbContext>()
            .AddConsumer<AppointmentsHandler>(Topology.AppointmentsQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
