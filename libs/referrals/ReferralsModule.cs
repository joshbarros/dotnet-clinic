using Clinic.Shared;
using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Clinic.Referrals;

public static class ReferralsModule
{
    public static IServiceCollection AddReferralsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Clinic") ?? throw new InvalidOperationException("Connection string 'Clinic' is not configured.");
        services.AddDbContext<ReferralsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", ReferralsDbContext.SchemaName)));
        services.AddScoped<ReferralsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<ReferralsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<ReferralsDbContext>>("referrals-db", tags: ["ready"]);

        messaging.AddOutbox<ReferralsDbContext>()
            .AddConsumer<ReferralsHandler>(Topology.ReferralsQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
