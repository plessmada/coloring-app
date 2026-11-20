using ColoringApp.Api.Authentication;
using ColoringApp.Api.Generations;
using Microsoft.EntityFrameworkCore;

namespace ColoringApp.Api.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    internal DbSet<GuestSession> GuestSessions => Set<GuestSession>();
    internal DbSet<Generation> Generations => Set<Generation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("coloring_app");

        var guest = modelBuilder.Entity<GuestSession>().ToTable("guest_sessions");
        guest.HasKey(session => session.Id);
        guest.Property(session => session.Id).HasColumnName("id");
        guest.Property(session => session.TokenDigest).HasColumnName("token_digest");
        guest.Property(session => session.CreatedAt).HasColumnName("created_at");
        guest.Property(session => session.ExpiresAt).HasColumnName("expires_at");

        var job = modelBuilder.Entity<Generation>().ToTable("generations");
        job.HasKey(generation => generation.Id);
        job.HasOne(generation => generation.GuestSession).WithMany()
            .HasForeignKey(generation => generation.GuestSessionId).OnDelete(DeleteBehavior.Restrict);
        job.Property(generation => generation.Id).HasColumnName("id");
        job.Property(generation => generation.GuestSessionId).HasColumnName("guest_session_id");
        job.Property(generation => generation.UserId).HasColumnName("user_id");
        job.Property(generation => generation.Status).HasColumnName("status").HasConversion<string>();
        job.Property(generation => generation.CreatedAt).HasColumnName("created_at");
        job.Property(generation => generation.StartedAt).HasColumnName("started_at");
        job.Property(generation => generation.CompletedAt).HasColumnName("completed_at");
        job.Property(generation => generation.AudioKey).HasColumnName("audio_key");
        job.Property(generation => generation.OwnerToken).HasColumnName("owner_token");
        job.Property(generation => generation.LeaseUntil).HasColumnName("lease_until");
        job.Property(generation => generation.DeadlineAt).HasColumnName("deadline_at");
        job.Property(generation => generation.ErrorCode).HasColumnName("error_code");
        job.Property(generation => generation.Transcript).HasColumnName("transcript");
        job.Property(generation => generation.FinalPrompt).HasColumnName("final_prompt");
        job.Property(generation => generation.Keywords).HasColumnName("keywords");
        job.Property(generation => generation.ImageKey).HasColumnName("image_key");
    }
}
