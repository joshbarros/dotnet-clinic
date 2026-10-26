using Clinic.Shared;
using Clinic.Shared.Auth;
using Clinic.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Scheduling;

public record CreateSessionRequest(string? Code, string? Provider, string? Specialty, string? Location, DateTimeOffset? StartsAt, long CapacitySlots);

public record SessionDto(Guid Id, string Code, string Provider, string Specialty, string Location, DateTimeOffset StartsAt, int CapacitySlots, int RemainingSlots)
{
    public static SessionDto From(Session s) => new(s.Id, s.Code, s.Provider, s.Specialty, s.Location, s.StartsAt, s.CapacitySlots, s.RemainingSlots);
}

/// <summary>What other modules may ask Scheduling, over Scheduling's own tables.</summary>
public sealed class SessionCatalog(SchedulingDbContext db) : ISessionCatalog
{
    public async Task<SessionInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Sessions.AsNoTracking().Where(s => s.Code == code).Select(s => new SessionInfo(s.Id, s.Code)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/sessions")]
[Authorize]
public class SessionsController(SchedulingDbContext db) : ControllerBase
{
    private const int MaxCapacitySlots = 200;

    [HttpGet]
    public async Task<IEnumerable<SessionDto>> List(CancellationToken ct) =>
        (await db.Sessions.AsNoTracking().OrderBy(s => s.StartsAt).ThenBy(s => s.Code).ToListAsync(ct)).Select(SessionDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<SessionDto>> Get(string code, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return session is null ? NotFound() : SessionDto.From(session);
    }

    /// <summary>Opens a session. Staff only. This is the only place RemainingSlots is initialised; after that only the Scheduling handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<SessionDto>> Create(CreateSessionRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Specialty) || string.IsNullOrWhiteSpace(request.Location))
            throw new DomainException(400, "Provider, specialty and location are required.");
        if (request.CapacitySlots < 1 || request.CapacitySlots > MaxCapacitySlots)
            throw new DomainException(400, $"Capacity must be between 1 and {MaxCapacitySlots} slots.");
        if (await db.Sessions.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Session '{code}' already exists.");

        var session = new Session
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Provider = request.Provider.Trim(),
            Specialty = request.Specialty.Trim(),
            Location = request.Location.Trim(),
            StartsAt = request.StartsAt ?? DateTimeOffset.UtcNow.AddDays(14),
            CapacitySlots = (int)request.CapacitySlots,
            RemainingSlots = (int)request.CapacitySlots,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return Created($"/api/sessions/{session.Code}", SessionDto.From(session));
    }
}
