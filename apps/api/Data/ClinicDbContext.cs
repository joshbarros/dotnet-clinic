using Clinic.Api.Messaging;
using Clinic.Api.Modules.Appointments;
using Clinic.Api.Modules.Scheduling;
using Clinic.Api.Modules.Referrals;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.Data;

public class ClinicDbContext(DbContextOptions<ClinicDbContext> options) : DbContext(options)
{
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SlotHold> Holds => Set<SlotHold>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<ReferralLetter> Letters => Set<ReferralLetter>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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

        b.Entity<Appointment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(AppointmentRules.MaxReferenceLength);
            e.HasIndex(x => new { x.SessionId, x.Reference }).IsUnique();
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId);
        });

        b.Entity<ReferralLetter>(e =>
        {
            e.ToTable("ReferralLetters");
            e.HasKey(x => x.AppointmentId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
