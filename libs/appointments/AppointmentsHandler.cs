using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Clinic.Appointments;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a appointment forward. Each step runs only from the status it expects.</summary>
public class AppointmentsHandler(AppointmentsDbContext db, IOptions<AppointmentOptions> options) : IMessageHandler
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
