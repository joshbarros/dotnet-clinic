using Clinic.Api.Data;
using Clinic.Api.Messaging;
using Clinic.Api.Modules.Scheduling;
using Clinic.Api.Modules.Referrals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Clinic.Api.Modules.Appointments;

public enum AppointmentStatus { Requested, Held, Confirmed, Cancelled, Rejected }

public class Appointment
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string SessionCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Slots { get; set; }
    public long FeeCents { get; set; }
    public AppointmentStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? LetterNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SlotsReleasedAt { get; set; }
}

public class AppointmentOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the cancellation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreateAppointmentRequest(string? SessionCode, string? Reference, long Slots);

public record AppointmentDto(
    Guid Id, string SessionCode, string Reference, int Slots, long FeeCents, string Status,
    string? Reason, string? LetterNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? SlotsReleasedAt)
{
    public static AppointmentDto From(Appointment b) => new(
        b.Id, b.SessionCode, b.Reference, b.Slots, b.FeeCents, b.Status.ToString(),
        b.Reason, b.LetterNumber, b.CreatedAt, b.UpdatedAt, b.SlotsReleasedAt);
}

/// <summary>HTTP side of the saga: writes the appointment and its first outbox row in one local transaction.</summary>
public class AppointmentService(ClinicDbContext db)
{
    public async Task<Appointment> CreateAsync(CreateAppointmentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SessionCode))
            throw new DomainException(400, "Session code is required.");
        var error = AppointmentRules.Validate(request.Reference, request.Slots);
        if (error is not null)
            throw new DomainException(400, error);

        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.SessionCode.Trim(), ct)
            ?? throw new DomainException(404, $"Session '{request.SessionCode}' does not exist.");

        var slots = (int)request.Slots;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var appointment = new Appointment
        {
            Id = Guid.CreateVersion7(),
            SessionId = session.Id,
            SessionCode = session.Code,
            Reference = reference,
            Slots = slots,
            FeeCents = AppointmentRules.FeeCents(slots),
            Status = AppointmentStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Appointments.Add(appointment);
        db.Enqueue("appointments", Topology.ReserveKey, appointment.Id, new ReserveSlots(appointment.Id, session.Id, slots, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {session.Code}.");
        }
        await tx.CommitAsync(ct);
        return appointment;
    }

    /// <summary>Moves Held or Issued to Cancelled and asks Scheduling to give the slot back. The referral letter is not touched.</summary>
    public async Task<Appointment> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Appointments
            .Where(b => b.Id == id && (b.Status == AppointmentStatus.Held || b.Status == AppointmentStatus.Confirmed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, AppointmentStatus.Cancelled)
                .SetProperty(b => b.Reason, "Cancelled by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Appointment not found.");
            throw new DomainException(409, $"An appointment in status {current.Status} cannot be cancelled.");
        }

        db.Enqueue("appointments", Topology.ReleaseKey, id, new ReleaseSlots(id, "cancellation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Appointments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a appointment forward. Each step runs only from the status it expects.</summary>
public class AppointmentsHandler(ClinicDbContext db, IOptions<AppointmentOptions> options) : IMessageHandler
{
    public const string Name = "appointments";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<SlotHeld>(d.Body).AppointmentId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<SlotShort>(d.Body), ct),
        Topology.IssuedKey => MarkConfirmedAsync(d.MessageId, Json.Parse<LetterIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<LetterRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<SlotReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Appointments does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid appointmentId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Appointments
            .Where(b => b.Id == appointmentId && b.Status == AppointmentStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, AppointmentStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, SlotShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Appointments
            .Where(b => b.Id == m.AppointmentId && b.Status == AppointmentStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, AppointmentStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedSlots} slots requested, {m.RemainingSlots} remaining.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a cancellation got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkConfirmedAsync(Guid messageId, LetterIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Appointments
            .Where(b => b.Id == m.AppointmentId && b.Status == AppointmentStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, AppointmentStatus.Confirmed)
                .SetProperty(b => b.LetterNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            appointmentIdForOrderCheck: m.AppointmentId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, LetterRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Appointments
            .Where(b => b.Id == m.AppointmentId && (b.Status == AppointmentStatus.Held || b.Status == AppointmentStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, AppointmentStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, SlotReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Appointments
            .Where(b => b.Id == m.AppointmentId && b.SlotsReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.SlotsReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? appointmentIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && appointmentIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Appointments.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == AppointmentStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
