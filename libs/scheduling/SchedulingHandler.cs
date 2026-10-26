using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Scheduling;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Session.RemainingSlots and of slot holds.
/// The reservation is one conditional UPDATE: it matches only while the remaining Slots still covers the request.
/// </summary>
public class SchedulingHandler(SchedulingDbContext db) : IMessageHandler
{
    public const string Name = "scheduling";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveSlots>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseSlots>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Scheduling does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveSlots command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Sessions
            .Where(s => s.Id == command.SessionId && s.RemainingSlots >= command.Slots)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemainingSlots, x => x.RemainingSlots - command.Slots), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new SlotHold
            {
                AppointmentId = command.AppointmentId,
                SessionId = command.SessionId,
                Slots = command.Slots,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("scheduling", Topology.HeldKey, command.AppointmentId,
                new SlotHeld(command.AppointmentId, command.OwnerId, command.SessionId, command.Slots, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Sessions.Where(s => s.Id == command.SessionId)
                .Select(s => s.RemainingSlots).FirstOrDefaultAsync(ct);
            db.Enqueue("scheduling", Topology.ShortKey, command.AppointmentId,
                new SlotShort(command.AppointmentId, command.Reference, command.Slots, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives a held unit back exactly once. A second release for the same appointment changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseSlots command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.AppointmentId == command.AppointmentId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.AppointmentId == command.AppointmentId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Sessions
                    .Where(s => s.Id == hold.SessionId && s.RemainingSlots + hold.Slots <= s.CapacitySlots)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemainingSlots, x => x.RemainingSlots + hold.Slots), ct);
                db.Enqueue("scheduling", Topology.ReleasedKey, command.AppointmentId,
                    new SlotReleased(command.AppointmentId, hold.Slots, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
