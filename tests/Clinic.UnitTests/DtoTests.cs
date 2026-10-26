using Clinic.Appointments;
using Clinic.Scheduling;
using Xunit;

namespace Clinic.UnitTests;

public class DtoTests
{
    [Fact]
    public void AppointmentDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            SessionCode = "S-1",
            Reference = "PT-1",
            Slots = 3,
            FeeCents = 277_500,
            Status = AppointmentStatus.Confirmed,
            LetterNumber = "RL-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = AppointmentDto.From(appointment);

        Assert.Equal(appointment.Id, dto.Id);
        Assert.Equal(appointment.OwnerId, dto.OwnerId);
        Assert.Equal("Confirmed", dto.Status);
        Assert.Equal(277_500, dto.FeeCents);
        Assert.Equal("RL-1", dto.LetterNumber);
    }

    [Fact]
    public void SessionDto_MapsTheScarceNumber()
    {
        var session = new Session { Id = Guid.NewGuid(), Code = "S-1", Provider = "v", Specialty = "o", Location = "d", CapacitySlots = 10, RemainingSlots = 4 };
        var dto = SessionDto.From(session);
        Assert.Equal(10, dto.CapacitySlots);
        Assert.Equal(4, dto.RemainingSlots);
    }
}
