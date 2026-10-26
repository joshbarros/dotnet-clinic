using Clinic.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Scheduling;

public class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "slots";

    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SlotHold> Holds => Set<SlotHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Session>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Sessions_Remaining", "\"RemainingSlots\" >= 0 AND \"RemainingSlots\" <= \"CapacitySlots\""));
        });

        b.Entity<SlotHold>(e =>
        {
            e.ToTable("SlotHolds");
            e.HasKey(x => x.AppointmentId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId);
        });
    }
}
