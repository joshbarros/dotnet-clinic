using Clinic.Appointments;
using Clinic.Referrals;
using Clinic.Shared;
using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Clinic.IntegrationTests;

public class SagaTests
{
    private static AppointmentsHandler Handler(AppointmentsDbContext db) => new(db, Options.Create(new AppointmentOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(5);
        var appointment = await TestDatabase.AddAppointmentAsync(session, "PT-POISON-1", 2, AppointmentStatus.Held);
        var held = new SlotHeld(appointment.Id, appointment.OwnerId, session.Id, 2, appointment.Reference);

        await using var docs = TestDatabase.Referrals();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new ReferralsHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Referrals();
        var doc = await check.ReferralLetters.SingleAsync(d => d.AppointmentId == appointment.Id);
        Assert.Equal(LetterStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(appointment.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesAppointmentCancelledAndLetterIssued()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(5);
        var owner = Guid.CreateVersion7();
        var appointment = await TestDatabase.AddAppointmentAsync(session, "PT-1", 2, AppointmentStatus.Held, owner);

        // The referral letter is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Referrals())
            await new ReferralsHandler(docs).IssueAsync(Guid.CreateVersion7(), new SlotHeld(appointment.Id, owner, session.Id, 2, appointment.Reference));

        // The owner reverses first.
        await using (var appointments = TestDatabase.Appointments())
            await new AppointmentService(appointments, new FixedCatalog(session), new TestUser(owner)).CancelAsync(appointment.Id, default);

        await using var late = TestDatabase.Appointments();
        var mark = await Handler(late).MarkConfirmedAsync(Guid.CreateVersion7(), new LetterIssued(appointment.Id, "RL-TEST"));

        await using var check = TestDatabase.Appointments();
        await using var checkDocs = TestDatabase.Referrals();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(AppointmentStatus.Cancelled, (await check.Appointments.SingleAsync()).Status);
        Assert.Equal(LetterStatus.Issued, (await checkDocs.ReferralLetters.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(5);
        var held = await TestDatabase.AddAppointmentAsync(session, "PT-A", 1, AppointmentStatus.Held);
        var rejected = await TestDatabase.AddAppointmentAsync(session, "PT-B", 1, AppointmentStatus.Rejected);
        var requested = await TestDatabase.AddAppointmentAsync(session, "PT-C", 1, AppointmentStatus.Requested);

        await using (var a = TestDatabase.Appointments())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkConfirmedAsync(Guid.CreateVersion7(), new LetterIssued(held.Id, "RL-1")));
        await using (var b = TestDatabase.Appointments())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkConfirmedAsync(Guid.CreateVersion7(), new LetterIssued(rejected.Id, "RL-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Appointments())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkConfirmedAsync(Guid.CreateVersion7(), new LetterIssued(requested.Id, "RL-3")));

        await using var check = TestDatabase.Appointments();
        var statuses = (await check.Appointments.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(AppointmentStatus.Confirmed, statuses["PT-A"]);
        Assert.Equal(AppointmentStatus.Rejected, statuses["PT-B"]);
        Assert.Equal(AppointmentStatus.Requested, statuses["PT-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedAppointment_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(5);
        var owner = Guid.CreateVersion7();
        var appointment = await TestDatabase.AddAppointmentAsync(session, "PT-1", 1, AppointmentStatus.Rejected, owner);

        await using var db = TestDatabase.Appointments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new AppointmentService(db, new FixedCatalog(session), new TestUser(owner)).CancelAsync(appointment.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Cancellation_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(5);
        var appointment = await TestDatabase.AddAppointmentAsync(session, "PT-1", 1, AppointmentStatus.Held);

        await using var db = TestDatabase.Appointments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new AppointmentService(db, new FixedCatalog(session), new TestUser(Guid.NewGuid())).CancelAsync(appointment.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Appointments();
        Assert.Equal(AppointmentStatus.Held, (await check.Appointments.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanCancelAnyonesAppointment()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(5);
        var appointment = await TestDatabase.AddAppointmentAsync(session, "PT-1", 1, AppointmentStatus.Held);

        await using var db = TestDatabase.Appointments();
        var cancelled = await new AppointmentService(db, new FixedCatalog(session), new TestUser(Guid.NewGuid(), staff: true)).CancelAsync(appointment.Id, default);
        Assert.Equal(AppointmentStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Appointments();
        var appointment = await new AppointmentService(db, new FixedCatalog(session), new TestUser(owner))
            .CreateAsync(new CreateAppointmentRequest(session.Code, "PT-1", 2), default);

        await using var check = TestDatabase.Appointments();
        Assert.Equal(owner, (await check.Appointments.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveSlots>(message.Payload).OwnerId);
        Assert.Equal(AppointmentStatus.Requested, appointment.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(5);
        await using var db = TestDatabase.Appointments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new AppointmentService(db, new FixedCatalog(session), new TestUser(null)).CreateAsync(new CreateAppointmentRequest(session.Code, "PT-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownSession_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Appointments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new AppointmentService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateAppointmentRequest("NOPE", "PT-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
