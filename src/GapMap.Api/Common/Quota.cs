using System.Security.Claims;
using GapMap.Api.Ai;
using GapMap.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GapMap.Api.Common;

public sealed class CurrentUser(IHttpContextAccessor http)
{
    private ClaimsPrincipal? P => http.HttpContext?.User;
    public Guid Id => Guid.TryParse(P?.FindFirstValue("uid"), out var g) ? g : Guid.Empty;
    public string Role => P?.FindFirstValue("role") ?? "user";
    public string Status => P?.FindFirstValue("status") ?? "pending";
    public bool IsApproved => Status == "approved";
    public bool IsAdmin => Role == "admin";
}

// Gate on the paid endpoints. Enforces:
//  1. the global hard stop against the shared API balance (the real protection), and
//  2. a per-user monthly application cap.
// The global cap matters most: one shared $5 balance means one runaway loop drains everyone.
public sealed class QuotaMiddleware(RequestDelegate next)
{
    // Paths include the /api prefix because FastEndpoints RoutePrefix is applied before middleware sees the path.
    private static readonly string[] Paid = ["/api/match", "/api/tailor", "/api/cover-letter", "/api/profile/parse"];

    public async Task Invoke(HttpContext ctx, GapMapDbContext db, CurrentUser user, QuotaOptions q, ILogger<QuotaMiddleware> log)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!Paid.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(ctx);
            return;
        }

        var total = await db.UsageEvents.SumAsync(e => (decimal?)e.CostUsd) ?? 0m;
        if (total >= q.GlobalHardStopUsd)
        {
            ctx.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await ctx.Response.WriteAsJsonAsync(new { error = "The shared API budget is exhausted. Top up to continue." });
            return;
        }
        if (total >= q.GlobalSoftAlertUsd)
            log.LogWarning("Quota soft alert: shared spend {Total:F2} has crossed {Soft:F2} (hard stop {Hard:F2}).",
                total, q.GlobalSoftAlertUsd, q.GlobalHardStopUsd);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var apps = await db.UsageEvents
            .Where(e => e.UserId == user.Id && e.Operation == "match" && e.CreatedAt >= monthStart)
            .CountAsync();
        if (apps >= q.PerUserApplicationsPerMonth)
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await ctx.Response.WriteAsJsonAsync(new { error = "Monthly application limit reached." });
            return;
        }

        await next(ctx);
    }
}
