namespace Clinic.Shared.Contracts;

public record SessionInfo(Guid Id, string Code);

/// <summary>
/// What Appointments may ask Scheduling, without referencing it. Scheduling implements it over its own tables;
/// Appointments never reads another module's schema.
/// </summary>
public interface ISessionCatalog
{
    Task<SessionInfo?> FindAsync(string code, CancellationToken ct);
}
