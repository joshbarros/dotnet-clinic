using System.Collections.Concurrent;
using Clinic.Scheduling;
using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.IntegrationTests;

public class SchedulingConcurrencyTests
{
    [Fact]
    public async Task LastUnit_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(capacitySlots: 1);

        var outcomes = new ConcurrentBag<(Guid Appointment, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var appointmentId = Guid.CreateVersion7();
            await using var db = TestDatabase.Scheduling();
            var outcome = await new SchedulingHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveSlots(appointmentId, Guid.NewGuid(), session.Id, 1, $"PT-{n}"));
            outcomes.Add((appointmentId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Scheduling();
        var remaining = await check.Sessions.Where(s => s.Id == session.Id).Select(s => s.RemainingSlots).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Appointment;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.AppointmentId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheUnitBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(capacitySlots: 4);
        var appointmentId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Scheduling())
            await new SchedulingHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveSlots(appointmentId, Guid.NewGuid(), session.Id, 3, "PT-1"));

        // Two different commands (poison compensation and a cancellation) both ask for the same unit back.
        await using var first = TestDatabase.Scheduling();
        await using var second = TestDatabase.Scheduling();
        var one = await new SchedulingHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSlots(appointmentId, "poison"));
        var two = await new SchedulingHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSlots(appointmentId, "cancellation"));

        await using var check = TestDatabase.Scheduling();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Sessions.Where(s => s.Id == session.Id).Select(s => s.RemainingSlots).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(capacitySlots: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveSlots(Guid.CreateVersion7(), Guid.NewGuid(), session.Id, 2, "PT-1");

        await using var a = TestDatabase.Scheduling();
        await using var b = TestDatabase.Scheduling();
        var first = await new SchedulingHandler(a).ReserveAsync(messageId, command);
        var second = await new SchedulingHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Scheduling();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Sessions.Where(s => s.Id == session.Id).Select(s => s.RemainingSlots).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(capacitySlots: 2);
        await using var db = TestDatabase.Scheduling();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Sessions.Where(s => s.Id == session.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemainingSlots, x => x.RemainingSlots - 3)));
    }
}
