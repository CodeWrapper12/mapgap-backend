using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FastEndpoints;
using GapMap.Api.Common;
using GapMap.Api.Infrastructure;
using Google.Apis.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using GapMap.Api.Ai;

namespace GapMap.Api.Features.Auth;

public sealed class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "gapmap";
    public string Audience { get; set; } = "gapmap";
    public int Minutes { get; set; } = 120;
}

// Status and role are JWT claims (cheap gate, no DB hit per request). Because a claim can
// go stale, re-issue the token whenever an admin changes a user's status (short lifetime helps).
public sealed class JwtTokenService(JwtOptions opts)
{
    public string Issue(User u, bool hasProfile = false)
    {
        var claims = new List<Claim>
        {
            new Claim("uid", u.Id.ToString()),
            new Claim("role", u.Role.ToString().ToLowerInvariant()),
            new Claim("status", u.Status.ToString().ToLowerInvariant()),
            new Claim(JwtRegisteredClaimNames.Email, u.Email),
            new Claim("has_profile", hasProfile.ToString().ToLowerInvariant())
        };

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.Key)), SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(opts.Issuer, opts.Audience, claims,
            expires: DateTime.UtcNow.AddMinutes(opts.Minutes), signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

// Sign in / sign up with a Google credential. New users require a valid voucher and land as Pending.
public sealed record GoogleAuthRequest(string IdToken);
public sealed record AuthResponse(string Token, string Status);

public sealed class GoogleAuthEndpoint(GapMapDbContext db, JwtTokenService jwt) : Endpoint<GoogleAuthRequest, AuthResponse>
{
    public override void Configure() { Post("/auth/google"); AllowAnonymous(); }

    public override async Task HandleAsync(GoogleAuthRequest req, CancellationToken ct)
    {
        GoogleJsonWebSignature.Payload payload;
        try { payload = await GoogleJsonWebSignature.ValidateAsync(req.IdToken); }
        catch { await SendUnauthorizedAsync(ct); return; }

        var user = await db.Users.FirstOrDefaultAsync(u => u.GoogleSub == payload.Subject, ct);
        if (user is null)
        {
            // Signup path: users are created as Pending by default.
            user = new User { GoogleSub = payload.Subject, Email = payload.Email, Name = payload.Name ?? "", Status = UserStatus.Pending };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }

        var hasProfile = await db.Profiles.AnyAsync(p => p.UserId == user.Id, ct);
        await SendOkAsync(new AuthResponse(jwt.Issue(user, hasProfile), user.Status.ToString().ToLowerInvariant()), ct);
    }
}

public sealed record RedeemVoucherRequest(string Code);
public sealed class RedeemVoucherEndpoint(GapMapDbContext db, CurrentUser me, JwtTokenService jwt) : Endpoint<RedeemVoucherRequest, AuthResponse>
{
    public override void Configure() { Post("/auth/redeem"); } // Requires authentication
    
    public override async Task HandleAsync(RedeemVoucherRequest req, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([me.Id], ct);
        if (user is null) { await SendNotFoundAsync(ct); return; }
        if (user.Status != UserStatus.Pending)
        {
            AddError("Account is already active.");
            await SendErrorsAsync(cancellation: ct); return;
        }

        var claimed = await db.Vouchers
            .Where(v => v.Code == req.Code && v.Uses < v.MaxUses
                        && (v.ExpiresAt == null || v.ExpiresAt > DateTime.UtcNow))
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Uses, v => v.Uses + 1), ct);

        if (claimed == 0)
        {
            AddError(r => r.Code, "Invalid or expired voucher code.");
            await SendErrorsAsync(cancellation: ct); return;
        }

        user.Status = UserStatus.Approved;
        await db.SaveChangesAsync(ct);

        var hasProfile = await db.Profiles.AnyAsync(p => p.UserId == user.Id, ct);
        await SendOkAsync(new AuthResponse(jwt.Issue(user, hasProfile), user.Status.ToString().ToLowerInvariant()), ct);
    }
}

public sealed class RefreshAuthEndpoint(GapMapDbContext db, CurrentUser me, JwtTokenService jwt) : EndpointWithoutRequest<AuthResponse>
{
    public override void Configure() { Post("/auth/refresh"); }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var user = await db.Users.FindAsync([me.Id], ct);
        if (user is null) { await SendUnauthorizedAsync(ct); return; }
        
        var hasProfile = await db.Profiles.AnyAsync(p => p.UserId == user.Id, ct);
        await SendOkAsync(new AuthResponse(jwt.Issue(user, hasProfile), user.Status.ToString().ToLowerInvariant()), ct);
    }
}

// ----- Admin -----
// All admin endpoints use the declarative .Policies("Admin") gate in Configure(),
// so the framework returns 401/403 before handler code runs. No manual checks needed.

public sealed class ListPendingEndpoint(GapMapDbContext db) : EndpointWithoutRequest<List<object>>
{
    public override void Configure() { Get("/admin/users"); Policies("Admin"); }
    public override async Task HandleAsync(CancellationToken ct)
    {
        var pending = await db.Users.Where(u => u.Status == UserStatus.Pending)
            .Select(u => (object)new { u.Id, u.Email, u.CreatedAt }).ToListAsync(ct);
        await SendOkAsync(pending, ct);
    }
}

public sealed record ApproveRequest(Guid Id, bool Approve);
public sealed class ApproveEndpoint(GapMapDbContext db, JwtTokenService jwt) : Endpoint<ApproveRequest, object>
{
    public override void Configure() { Post("/admin/users/approve"); Policies("Admin"); }
    public override async Task HandleAsync(ApproveRequest req, CancellationToken ct)
    {
        var u = await db.Users.FindAsync([req.Id], ct);
        if (u is null) { await SendNotFoundAsync(ct); return; }
        u.Status = req.Approve ? UserStatus.Approved : UserStatus.Disabled;
        await db.SaveChangesAsync(ct);
        // Return a fresh token for the affected user so the admin can share it or
        // the system can push it (mitigates the stale-claims window).
        await SendOkAsync(new { newToken = jwt.Issue(u) }, ct);
    }
}

public sealed record CreateVoucherRequest(int MaxUses, DateTime? ExpiresAt);
public sealed class CreateVoucherEndpoint(GapMapDbContext db, CurrentUser me) : Endpoint<CreateVoucherRequest, object>
{
    public override void Configure() { Post("/admin/vouchers"); Policies("Admin"); }
    public override async Task HandleAsync(CreateVoucherRequest req, CancellationToken ct)
    {
        var code = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..10];
        db.Vouchers.Add(new Voucher { Code = code, CreatedBy = me.Id, MaxUses = req.MaxUses, ExpiresAt = req.ExpiresAt });
        await db.SaveChangesAsync(ct);
        await SendOkAsync(new { code }, ct);
    }
}

public sealed class UsageDashboardEndpoint(GapMapDbContext db, QuotaOptions opts) : EndpointWithoutRequest<object>
{
    public override void Configure() { Get("/admin/usage"); Policies("Admin"); }
    public override async Task HandleAsync(CancellationToken ct)
    {
        var usageStats = await db.UsageEvents
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, Cost = g.Sum(x => x.CostUsd), Calls = g.Count() })
            .ToListAsync(ct);

        var total = await db.UsageEvents.SumAsync(e => (decimal?)e.CostUsd) ?? 0m;

        var mostUsedOperation = await db.UsageEvents
            .GroupBy(e => e.Operation)
            .OrderByDescending(g => g.Sum(x => x.InputTokens + x.OutputTokens))
            .Select(g => g.Key)
            .FirstOrDefaultAsync(ct);

        var users = await db.Users.ToListAsync(ct);

        var enrichedUsers = users.Select(u => {
            var stats = usageStats.FirstOrDefault(s => s.UserId == u.Id);
            return new {
                Id = u.Id,
                Email = u.Email,
                Name = u.Name,
                Status = u.Status.ToString(),
                Cost = stats?.Cost ?? 0m,
                Calls = stats?.Calls ?? 0
            };
        }).OrderByDescending(u => u.Cost).ToList();

        await SendOkAsync(new { 
            total, 
            limit = opts.GlobalHardStopUsd,
            remaining = Math.Max(0, opts.GlobalHardStopUsd - total),
            mostTokenUsedFor = mostUsedOperation ?? "none",
            users = enrichedUsers 
        }, ct);
    }
}

public sealed class SeedPendingEndpoint(GapMapDbContext db) : EndpointWithoutRequest<object>
{
    public override void Configure() { Get("/admin/seed-pending"); AllowAnonymous(); }
    public override async Task HandleAsync(CancellationToken ct)
    {
        db.Users.Add(new User { GoogleSub = Guid.NewGuid().ToString(), Email = "pending_test@example.com", Name = "Pending Dummy", Status = UserStatus.Pending });
        await db.SaveChangesAsync(ct);
        await SendOkAsync(new { success = true }, ct);
    }
}

public sealed record UpdateUserRequest(string Name);
public sealed class UpdateUserEndpoint(GapMapDbContext db) : Endpoint<UpdateUserRequest, object>
{
    public override void Configure() { Patch("/admin/users/{id}"); Policies("Admin"); }
    public override async Task HandleAsync(UpdateUserRequest req, CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var user = await db.Users.FindAsync([id], ct);
        if (user is null) { await SendNotFoundAsync(ct); return; }
        user.Name = req.Name;
        await db.SaveChangesAsync(ct);
        await SendOkAsync(new { success = true }, ct);
    }
}

