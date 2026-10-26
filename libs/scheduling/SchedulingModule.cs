using Clinic.Shared;
using Clinic.Shared.Contracts;
using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Scheduling;

public static class SchedulingModule
{
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Clinic") ?? throw new InvalidOperationException("Connection string 'Clinic' is not configured.");
        services.AddDbContext<SchedulingDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", SchedulingDbContext.SchemaName)));
        services.AddScoped<SchedulingHandler>();
        services.AddScoped<ISessionCatalog, SessionCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<SchedulingDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<SchedulingDbContext>>("slots-db", tags: ["ready"]);

        messaging.AddOutbox<SchedulingDbContext>()
            .AddConsumer<SchedulingHandler>(Topology.SchedulingQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
