using Clinic.Api.Data;
using Clinic.Api.Modules.Appointments;
using Clinic.Api.Modules.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CLINIC_TEST_DB")
        ?? "Host=localhost;Port=5471;Database=clinic_test;Username=clinic;Password=clinic";

    private static readonly DbContextOptions<ClinicDbContext> Options =
        new DbContextOptionsBuilder<ClinicDbContext>().UseNpgsql(ConnectionString).Options;

    public static ClinicDbContext Create() => new(Options);

    public static async Task<ClinicDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"ReferralLetters\", \"SlotHolds\", \"Appointments\", \"Sessions\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Session> AddSessionAsync(ClinicDbContext db, int capacitySlots)
    {
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

    public static async Task<Appointment> AddAppointmentAsync(ClinicDbContext db, Session session, string reference, int slots, AppointmentStatus status)
    {
        var appointment = new Appointment
        {
            Id = Guid.CreateVersion7(),
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
