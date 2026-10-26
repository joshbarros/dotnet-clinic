using System.Text.Json;
using Clinic.Api.Data;
using Clinic.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Modules.Appointments;

[ApiController]
[Route("api/appointments")]
public class AppointmentsController(ClinicDbContext db, AppointmentService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the appointment. The slot hold and the referral letter follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<AppointmentDto>> Create(CreateAppointmentRequest request, CancellationToken ct)
    {
        var appointment = await service.CreateAsync(request, ct);
        return Accepted($"/api/appointments/{appointment.Id}", AppointmentDto.From(appointment));
    }

    [HttpGet]
    public async Task<IEnumerable<AppointmentDto>> List([FromQuery] string? sessionCode, CancellationToken ct)
    {
        var query = db.Appointments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(sessionCode))
            query = query.Where(b => b.SessionCode == sessionCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(AppointmentDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AppointmentDto>> Get(Guid id, CancellationToken ct)
    {
        var appointment = await db.Appointments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return appointment is null ? NotFound() : AppointmentDto.From(appointment);
    }

    [HttpPost("{id:guid}/cancellation")]
    public async Task<ActionResult<AppointmentDto>> Cancel(Guid id, CancellationToken ct) =>
        Accepted($"/api/appointments/{id}", AppointmentDto.From(await service.CancelAsync(id, ct)));

    /// <summary>The Kafka event log for one appointment, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
