using Clinic.Api.Data;
using Clinic.Api.Messaging;
using Clinic.Api.Modules.Appointments;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Modules.Referrals;

public enum LetterStatus { Issued, Rejected }

public class ReferralLetter
{
    public Guid AppointmentId { get; set; }
    public string? Number { get; set; }
    public LetterStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the referral letter once the slot is held. A poison reference is committed as rejected.</summary>
public class ReferralsHandler(ClinicDbContext db) : IMessageHandler
{
    public const string Name = "referrals";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Referrals does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<SlotHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, SlotHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (AppointmentRules.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Letters.Add(new ReferralLetter
            {
                AppointmentId = held.AppointmentId,
                Status = LetterStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("referrals", Topology.ReleaseKey, held.AppointmentId, new ReleaseSlots(held.AppointmentId, "poison"));
            db.Enqueue("referrals", Topology.RejectedKey, held.AppointmentId, new LetterRejected(held.AppointmentId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"RL-{held.AppointmentId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Letters.Add(new ReferralLetter
            {
                AppointmentId = held.AppointmentId,
                Number = number,
                Status = LetterStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("referrals", Topology.IssuedKey, held.AppointmentId, new LetterIssued(held.AppointmentId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
