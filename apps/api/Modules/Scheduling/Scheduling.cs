namespace Clinic.Api.Modules.Scheduling;

public class Session
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Specialty { get; set; } = "";
    public string Location { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public int CapacitySlots { get; set; }
    public int RemainingSlots { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class SlotHold
{
    public Guid AppointmentId { get; set; }
    public Guid SessionId { get; set; }
    public int Slots { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
