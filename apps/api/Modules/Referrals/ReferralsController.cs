using Clinic.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Modules.Referrals;

public record ReferralLetterDto(Guid AppointmentId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/letters")]
public class ReferralsController(ClinicDbContext db) : ControllerBase
{
    [HttpGet("{appointmentId:guid}")]
    public async Task<ActionResult<ReferralLetterDto>> Get(Guid appointmentId, CancellationToken ct)
    {
        var doc = await db.Letters.AsNoTracking().FirstOrDefaultAsync(d => d.AppointmentId == appointmentId, ct);
        return doc is null
            ? NotFound()
            : new ReferralLetterDto(doc.AppointmentId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
