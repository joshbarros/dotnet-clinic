using Clinic.Shared;
using Clinic.Shared.Auth;
using Clinic.Shared.Contracts;
using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Clinic.Appointments;

/// <summary>HTTP side of the saga: writes the appointment and its first outbox row in one local transaction.</summary>
public class AppointmentService(AppointmentsDbContext db, ISessionCatalog sessions, ICurrentUser user)
{
    public async Task<Appointment> CreateAsync(CreateAppointmentRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.SessionCode))
            throw new DomainException(400, "Session code is required.");
        var error = AppointmentRules.Validate(request.Reference, request.Slots);
        if (error is not null)
            throw new DomainException(400, error);

        var session = await sessions.FindAsync(request.SessionCode.Trim(), ct)
            ?? throw new DomainException(404, $"Session '{request.SessionCode}' does not exist.");

        var slots = (int)request.Slots;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var appointment = new Appointment
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("appointments", Topology.ReserveKey, appointment.Id, new ReserveSlots(appointment.Id, ownerId, session.Id, slots, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Appointment> GetAsync(Guid id, CancellationToken ct)
    {
        var appointment = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (appointment is null || !OwnerAccess.CanAccess(user, appointment.OwnerId))
            throw new DomainException(404, "Appointment not found.");
        return appointment;
    }

    public async Task<List<Appointment>> ListAsync(string? sessionCode, CancellationToken ct)
    {
        var query = db.Appointments.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see appointments.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(sessionCode))
            query = query.Where(b => b.SessionCode == sessionCode);
        return await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
    }

    /// <summary>Moves Held or Issued to Cancelled and asks Scheduling to give the slot back. The referral letter is not touched.</summary>
    public async Task<Appointment> CancelAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Appointments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"An appointment in status {current.Status} cannot be cancelled.");
        }

        db.Enqueue("appointments", Topology.ReleaseKey, id, new ReleaseSlots(id, "cancellation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Appointments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
