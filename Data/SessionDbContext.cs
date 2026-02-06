using Microsoft.EntityFrameworkCore;
using SessionApp.Data.Entities;

namespace SessionApp.Data
{
    public class SessionDbContext : DbContext
    {
        public SessionDbContext(DbContextOptions<SessionDbContext> options) : base(options) { }
        public DbSet<SessionEntity> Sessions { get; set; }
        public DbSet<ParticipantEntity> Participants { get; set; }
        public DbSet<GroupEntity> Groups { get; set; }
        public DbSet<GroupParticipantEntity> GroupParticipants { get; set; }
        public DbSet<ArchivedRoundEntity> ArchivedRounds { get; set; }
        public DbSet<CommanderEntity> Commanders { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SessionEntity>(entity =>
            {
                entity.HasKey(e => e.Code);
                entity.Property(e => e.Code).HasMaxLength(10);
                entity.Property(e => e.EventName).HasMaxLength(200).HasDefaultValue(string.Empty);
                entity.Property(e => e.HostId).IsRequired().HasMaxLength(100);
                entity.Property(e => e.SettingsJson).HasDefaultValue("{}").HasColumnType("jsonb");
                entity.Property(e => e.WinnerParticipantId).HasMaxLength(100);
                entity.Property(e => e.Archived).HasDefaultValue(false);
                entity.HasIndex(e => e.ExpiresAtUtc);
                entity.HasIndex(e => e.Archived);
            });

            modelBuilder.Entity<ParticipantEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SessionCode).IsRequired().HasMaxLength(10);
                entity.Property(e => e.ParticipantId).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Name).HasMaxLength(200);
                entity.Property(e => e.Commander).HasMaxLength(200).HasDefaultValue(string.Empty);
                entity.Property(e => e.Points).HasDefaultValue(0);
                entity.HasIndex(e => new { e.SessionCode, e.ParticipantId }).IsUnique();
                entity.HasOne(e => e.Session)
                    .WithMany(s => s.Participants)
                    .HasForeignKey(e => e.SessionCode)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<GroupEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SessionCode).IsRequired().HasMaxLength(10);
                entity.Property(e => e.WinnerParticipantId).HasMaxLength(100);
                entity.Property(e => e.StatisticsJson).HasDefaultValue("{}").HasColumnType("jsonb");
                entity.Property(e => e.RoundStarted).ValueGeneratedOnAdd().HasDefaultValue(false);
                entity.HasOne(e => e.Session)
                    .WithMany(s => s.Groups)
                    .HasForeignKey(e => e.SessionCode)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(e => e.ArchivedRound)
                    .WithMany(a => a.Groups)
                    .HasForeignKey(e => e.ArchivedRoundId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.HasIndex(e => e.RoundNumber);
                entity.HasIndex(e => e.SessionCode);
            });

            modelBuilder.Entity<GroupParticipantEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ParticipantId).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Name).HasMaxLength(200);
                entity.HasOne(e => e.Group)
                    .WithMany(g => g.GroupParticipants)
                    .HasForeignKey(e => e.GroupId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ArchivedRoundEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.SessionCode).IsRequired().HasMaxLength(10);
                entity.Property(e => e.Commander).HasMaxLength(200).HasDefaultValue(string.Empty);
                entity.Property(e => e.TurnCount).HasDefaultValue(-1);
                entity.Property(e => e.StatisticsJson).HasDefaultValue("{}").HasColumnType("jsonb");
                entity.HasOne(e => e.Session)
                    .WithMany(s => s.ArchivedRounds)
                    .HasForeignKey(e => e.SessionCode)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(e => new { e.SessionCode, e.RoundNumber });
            });

            modelBuilder.Entity<CommanderEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(300);
                entity.Property(e => e.ScryfallUri).IsRequired().HasMaxLength(500);
                entity.Property(e => e.LegalitiesJson).HasDefaultValue("{}").HasColumnType("jsonb");
                entity.HasIndex(e => e.Name);
                entity.HasIndex(e => e.LastUpdatedUtc);
            });
        }
    }
}