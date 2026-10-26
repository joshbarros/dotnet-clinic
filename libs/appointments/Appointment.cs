namespace Clinic.Appointments;

public enum AppointmentStatus { Requested, Held, Confirmed, Cancelled, Rejected }

public class Appointment
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
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

public record CreateAppointmentRequest(string? SessionCode, string? Reference, long Slots);

public record AppointmentDto(
    Guid Id, Guid OwnerId, string SessionCode, string Reference, int Slots, long FeeCents, string Status,
    string? Reason, string? LetterNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? SlotsReleasedAt)
{
    public static AppointmentDto From(Appointment b) => new(
        b.Id, b.OwnerId, b.SessionCode, b.Reference, b.Slots, b.FeeCents, b.Status.ToString(),
        b.Reason, b.LetterNumber, b.CreatedAt, b.UpdatedAt, b.SlotsReleasedAt);
}
