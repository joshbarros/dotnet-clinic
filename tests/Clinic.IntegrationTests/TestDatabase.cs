using Clinic.Appointments;
using Clinic.Scheduling;
using Clinic.Referrals;
using Clinic.Shared.Auth;
using Clinic.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Clinic.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CLINIC_TEST_DB")
        ?? "Host=localhost;Port=5471;Database=clinic_test;Username=clinic;Password=clinic";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static AppointmentsDbContext Appointments() => new(Options<AppointmentsDbContext>(AppointmentsDbContext.SchemaName));
    public static SchedulingDbContext Scheduling() => new(Options<SchedulingDbContext>(SchedulingDbContext.SchemaName));
    public static ReferralsDbContext Referrals() => new(Options<ReferralsDbContext>(ReferralsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var appointments = Appointments();
        await using var slots = Scheduling();
        await using var referrals = Referrals();
        await appointments.Database.MigrateAsync();
        await slots.Database.MigrateAsync();
        await referrals.Database.MigrateAsync();
        await appointments.Database.ExecuteSqlRawAsync(
            "TRUNCATE appointments.\"InboxMessages\", appointments.\"OutboxMessages\", appointments.\"Appointments\", " +
            "slots.\"InboxMessages\", slots.\"OutboxMessages\", slots.\"SlotHolds\", slots.\"Sessions\", " +
            "referrals.\"InboxMessages\", referrals.\"OutboxMessages\", referrals.\"ReferralLetters\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Session> AddSessionAsync(int capacitySlots)
    {
        await using var db = Scheduling();
        var session = new Session
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Provider = "MV Test",
            Specialty = "Cardiology",
            Location = "Clinic A",
            StartsAt = DateTimeOffset.UtcNow.AddDays(7),
            CapacitySlots = capacitySlots,
            RemainingSlots = capacitySlots,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    public static async Task<Appointment> AddAppointmentAsync(Session session, string reference, int slots, AppointmentStatus status, Guid? ownerId = null)
    {
        await using var db = Appointments();
        var appointment = new Appointment
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            SessionId = session.Id,
            SessionCode = session.Code,
            Reference = reference,
            Slots = slots,
            FeeCents = AppointmentRules.FeeCents(slots),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        return appointment;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Session[] sessions) : ISessionCatalog
{
    public Task<SessionInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(sessions.Where(s => s.Code == code).Select(s => new SessionInfo(s.Id, s.Code)).FirstOrDefault());
}
