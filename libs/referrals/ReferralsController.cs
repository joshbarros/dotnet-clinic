using Clinic.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Referrals;

public record ReferralLetterDto(Guid AppointmentId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/letters")]
[Authorize]
public class ReferralsController(ReferralsDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{appointmentId:guid}")]
    public async Task<ActionResult<ReferralLetterDto>> Get(Guid appointmentId, CancellationToken ct)
    {
        var doc = await db.ReferralLetters.AsNoTracking().FirstOrDefaultAsync(d => d.AppointmentId == appointmentId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new ReferralLetterDto(doc.AppointmentId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
