namespace Clinic.Api.Modules.Appointments;

public static class AppointmentRules
{
    public const int MaxSlotsPerAppointment = 4;
    public const int MaxReferenceLength = 40;
    public const string PoisonToken = "POISON";
    public const long FeeCentsPerSlot = 12_000;
    public const long AdminFeeCents = 1_500;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long slots)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Patient reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Patient reference must be at most {MaxReferenceLength} characters.";
        if (slots < 1)
            return "An appointment needs at least 1 slot.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (slots > MaxSlotsPerAppointment)
            return $"An appointment takes at most {MaxSlotsPerAppointment} slots.";
        return null;
    }

    /// <summary>Case-sensitive substring match: a longer token that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(PoisonToken, StringComparison.Ordinal);

    /// <summary>Fee per slot plus a flat admin fee, in cents. Overflow throws instead of wrapping.</summary>
    public static long FeeCents(int slots) => checked(slots * FeeCentsPerSlot + AdminFeeCents);
}
