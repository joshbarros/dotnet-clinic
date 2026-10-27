using System.Text.Json;
using Clinic.Shared.Auth;
using Clinic.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Appointments;

[ApiController]
[Route("api/appointments")]
[Authorize]
public class AppointmentsController(AppointmentService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the appointment for the signed-in user. Scheduling and the referral letter follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<AppointmentDto>> Create(CreateAppointmentRequest request, CancellationToken ct)
    {
        var appointment = await service.CreateAsync(request, ct);
        return Accepted($"/api/appointments/{appointment.Id}", AppointmentDto.From(appointment));
    }

    /// <summary>Your appointments, newest first, a page at a time (default 100, at most 100). Staff see everyone's. The total is in X-Total-Count.</summary>
    [HttpGet]
    public async Task<IEnumerable<AppointmentDto>> List([FromQuery] string? sessionCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        var (items, total) = await service.ListAsync(sessionCode, Math.Max(page, 1), Math.Clamp(pageSize, 1, 100), ct);
        Response.Headers["X-Total-Count"] = total.ToString();
        return items.Select(AppointmentDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<AppointmentDto> Get(Guid id, CancellationToken ct) => AppointmentDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/cancellation")]
    public async Task<ActionResult<AppointmentDto>> Cancel(Guid id, CancellationToken ct) =>
        Accepted($"/api/appointments/{id}", AppointmentDto.From(await service.CancelAsync(id, ct)));

    /// <summary>The Kafka event log for one appointment, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
