using Clinic.Api.Messaging;
using Clinic.Api.Modules.Appointments;
using Clinic.Api.Modules.Scheduling;
using Clinic.Api.Modules.Referrals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Clinic.Api.Tests;

public class SagaTests
{
    private static AppointmentsHandler Appointments() =>
        new(TestDatabase.Create(), Options.Create(new AppointmentOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(db, 5);
        var appointment = await TestDatabase.AddAppointmentAsync(db, session, "PT-POISON-1", 2, AppointmentStatus.Held);
        var held = new SlotHeld(appointment.Id, session.Id, 2, appointment.Reference);

        var handler = new ReferralsHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Letters.SingleAsync(d => d.AppointmentId == appointment.Id);
        Assert.Equal(LetterStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesAppointmentCancelledAndLetterIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(db, 5);
        var appointment = await TestDatabase.AddAppointmentAsync(db, session, "PT-1", 2, AppointmentStatus.Held);

        // The referral letter is issued; the final mark has not landed yet.
        var number = "RL-TEST";
        await new ReferralsHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new SlotHeld(appointment.Id, session.Id, 2, appointment.Reference));

        // The cancellation gets there first.
        await new AppointmentService(TestDatabase.Create()).CancelAsync(appointment.Id, default);

        var mark = await Appointments().MarkConfirmedAsync(Guid.CreateVersion7(), new LetterIssued(appointment.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(AppointmentStatus.Cancelled, (await check.Appointments.SingleAsync()).Status);
        Assert.Equal(LetterStatus.Issued, (await check.Letters.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(db, 5);
        var held = await TestDatabase.AddAppointmentAsync(db, session, "PT-A", 1, AppointmentStatus.Held);
        var rejected = await TestDatabase.AddAppointmentAsync(db, session, "PT-B", 1, AppointmentStatus.Rejected);
        var requested = await TestDatabase.AddAppointmentAsync(db, session, "PT-C", 1, AppointmentStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Appointments().MarkConfirmedAsync(Guid.CreateVersion7(), new LetterIssued(held.Id, "RL-1")));
        Assert.Equal(MarkOutcome.Skipped, await Appointments().MarkConfirmedAsync(Guid.CreateVersion7(), new LetterIssued(rejected.Id, "RL-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Appointments().MarkConfirmedAsync(Guid.CreateVersion7(), new LetterIssued(requested.Id, "RL-3")));

        var statuses = (await TestDatabase.Create().Appointments.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(AppointmentStatus.Confirmed, statuses["PT-A"]);
        Assert.Equal(AppointmentStatus.Rejected, statuses["PT-B"]);
        Assert.Equal(AppointmentStatus.Requested, statuses["PT-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedAppointment_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(db, 5);
        var appointment = await TestDatabase.AddAppointmentAsync(db, session, "PT-1", 1, AppointmentStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new AppointmentService(TestDatabase.Create()).CancelAsync(appointment.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
