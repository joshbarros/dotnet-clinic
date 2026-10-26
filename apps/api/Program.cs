using Clinic.Api.Data;
using Clinic.Api.Messaging;
using Clinic.Api.Middleware;
using Clinic.Api.Modules.Appointments;
using Clinic.Api.Modules.Scheduling;
using Clinic.Api.Modules.Referrals;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Clinic")
    ?? throw new InvalidOperationException("Connection string 'Clinic' is not configured.");
builder.Services.AddDbContext<ClinicDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<RabbitOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<AppointmentOptions>(builder.Configuration.GetSection("Appointments"));
builder.Services.AddSingleton<RabbitConnection>();
builder.Services.AddSingleton<KafkaLog>();

builder.Services.AddScoped<AppointmentService>();
builder.Services.AddScoped<SchedulingHandler>();
builder.Services.AddScoped<ReferralsHandler>();
builder.Services.AddScoped<AppointmentsHandler>();

builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService(sp => new ModuleConsumer<SchedulingHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.SchedulingQueue, [Topology.ReserveKey, Topology.ReleaseKey], Prefetch: 1),
    sp.GetRequiredService<ILogger<ModuleConsumer<SchedulingHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<ReferralsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.ReferralsQueue, [Topology.HeldKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<ReferralsHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<AppointmentsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.AppointmentsQueue,
        [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<AppointmentsHandler>>>()));

var app = builder.Build();

app.UseMiddleware<ExceptionHandler>();
app.MapControllers();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/api/health", () => Results.Text("Healthy"));

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ClinicDbContext>().Database.MigrateAsync();
}

app.Run();
