using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Appointments;

public class AppointmentsDbContext(DbContextOptions<AppointmentsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "appointments";

    public DbSet<Appointment> Appointments => Set<Appointment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Appointment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(AppointmentRules.MaxReferenceLength);
            e.HasIndex(x => new { x.SessionId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}
