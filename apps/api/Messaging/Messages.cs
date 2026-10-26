using System.Text.Json;

namespace Clinic.Api.Messaging;

public record ReserveSlots(Guid AppointmentId, Guid SessionId, int Slots, string Reference);
public record SlotHeld(Guid AppointmentId, Guid SessionId, int Slots, string Reference);
public record SlotShort(Guid AppointmentId, string Reference, int RequestedSlots, int RemainingSlots);
public record ReleaseSlots(Guid AppointmentId, string Reason);
public record SlotReleased(Guid AppointmentId, int Slots, string Reason);
public record LetterIssued(Guid AppointmentId, string Number);
public record LetterRejected(Guid AppointmentId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
