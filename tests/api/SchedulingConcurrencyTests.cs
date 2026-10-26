using System.Collections.Concurrent;
using Clinic.Api.Messaging;
using Clinic.Api.Modules.Scheduling;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Clinic.Api.Tests;

public class SchedulingConcurrencyTests
{
    [Fact]
    public async Task LastUnit_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(db, capacitySlots: 1);

        var outcomes = new ConcurrentBag<(Guid Appointment, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var appointmentId = Guid.CreateVersion7();
            var handler = new SchedulingHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveSlots(appointmentId, session.Id, 1, $"PT-{n}"));
            outcomes.Add((appointmentId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
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
        var db = await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(db, capacitySlots: 4);
        var appointmentId = Guid.CreateVersion7();
        var handler = new SchedulingHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSlots(appointmentId, session.Id, 3, "PT-1"));

        // Two different commands (poison compensation and a cancellation) both ask for the same unit back.
        var first = await new SchedulingHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseSlots(appointmentId, "poison"));
        var second = await new SchedulingHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseSlots(appointmentId, "cancellation"));

        var remaining = await TestDatabase.Create().Sessions.Where(s => s.Id == session.Id).Select(s => s.RemainingSlots).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var session = await TestDatabase.AddSessionAsync(db, capacitySlots: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveSlots(Guid.CreateVersion7(), session.Id, 2, "PT-1");

        var first = await new SchedulingHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new SchedulingHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Sessions.Where(s => s.Id == session.Id).Select(s => s.RemainingSlots).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}
