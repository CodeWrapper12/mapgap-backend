using Microsoft.EntityFrameworkCore;

namespace GapMap.Api.Infrastructure;

public enum UserStatus { Pending, Approved, Disabled }
public enum UserRole { User, Admin }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string GoogleSub { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
    public UserStatus Status { get; set; } = UserStatus.Pending;
    public string PlanTier { get; set; } = "free";
    public decimal? MonthlyTokenBudgetUsd { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Voucher
{
    public string Code { get; set; } = "";
    public Guid? CreatedBy { get; set; }
    public int MaxUses { get; set; } = 1;
    public int Uses { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsValid() =>
        Uses < MaxUses && (ExpiresAt is null || ExpiresAt > DateTime.UtcNow);
}

public class ProfileRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ProfileJson { get; set; } = "{}"; // jsonb — CandidateProfile
    public int Version { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ApplicationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string JdHash { get; set; } = "";
    public string JdText { get; set; } = "";
    public int Score { get; set; }
    public string MatchJson { get; set; } = "{}"; // jsonb — MatchResult
    public string? TargetTitle { get; set; }       // best-effort from JD; used as the CV header title
    public string? TailoredJson { get; set; }       // jsonb — validated TailorResult, read by export/cover-letter
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SkillGap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Skill { get; set; } = "";
    public string Status { get; set; } = "surfaced"; // surfaced | learning | closed (internal)
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class UsageEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string Operation { get; set; } = ""; // parse | match | tailor | cover_letter
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal CostUsd { get; set; } // computed AND stored at write time
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class GapMapDbContext(DbContextOptions<GapMapDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<ProfileRecord> Profiles => Set<ProfileRecord>();
    public DbSet<ApplicationRecord> Applications => Set<ApplicationRecord>();
    public DbSet<SkillGap> SkillGaps => Set<SkillGap>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Voucher>().HasKey(v => v.Code);
        b.Entity<User>().HasIndex(u => u.GoogleSub).IsUnique();
        b.Entity<ApplicationRecord>().HasIndex(a => new { a.UserId, a.JdHash });
        b.Entity<UsageEvent>().HasIndex(u => new { u.UserId, u.CreatedAt });

        // jsonb columns
        b.Entity<ProfileRecord>().Property(p => p.ProfileJson).HasColumnType("jsonb");
        b.Entity<ApplicationRecord>().Property(a => a.MatchJson).HasColumnType("jsonb");
        b.Entity<ApplicationRecord>().Property(a => a.TailoredJson).HasColumnType("jsonb");
    }
}
