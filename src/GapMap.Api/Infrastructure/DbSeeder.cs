using Microsoft.EntityFrameworkCore;

namespace GapMap.Api.Infrastructure;

/// <summary>
/// Seeds essential data on startup: an admin user and a default voucher.
/// Runs idempotently — skips if the data already exists.
/// </summary>
public static class DbSeeder
{
    // Fixed ID so the seeder is idempotent across restarts.
    private static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string DefaultVoucherCode = "GAPMAP2025";

    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GapMapDbContext>();

        // Ensure the database schema is up to date.
        await db.Database.MigrateAsync();

        await SeedAdminAsync(db);
        await SeedVoucherAsync(db);
    }

    private static async Task SeedAdminAsync(GapMapDbContext db)
    {
        if (await db.Users.AnyAsync(u => u.Id == AdminId))
            return;

        db.Users.Add(new User
        {
            Id = AdminId,
            GoogleSub = "admin-seeded",          // placeholder — update after first real Google sign-in
            Email = "admin@gapmap.local",
            Role = UserRole.Admin,
            Status = UserStatus.Approved,
            PlanTier = "admin",
            CreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedVoucherAsync(GapMapDbContext db)
    {
        if (await db.Vouchers.AnyAsync(v => v.Code == DefaultVoucherCode))
            return;

        db.Vouchers.Add(new Voucher
        {
            Code = DefaultVoucherCode,
            CreatedBy = AdminId,
            MaxUses = 50,                        // generous default for early testers
            Uses = 0,
            ExpiresAt = null,                    // no expiry
            CreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }
}
