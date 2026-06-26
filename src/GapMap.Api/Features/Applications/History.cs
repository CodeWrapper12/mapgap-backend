using FastEndpoints;
using GapMap.Api.Common;
using GapMap.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GapMap.Api.Features.Applications;

// GET /applications — the user's history (also the progress story). Newest first.
public sealed record HistoryItem(Guid Id, string? TargetTitle, int Score, bool HasTailored, DateTime CreatedAt);

public sealed class HistoryEndpoint(GapMapDbContext db, CurrentUser user) : EndpointWithoutRequest<List<HistoryItem>>
{
    public override void Configure() { Get("/applications"); }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }
        var items = await db.Applications
            .Where(a => a.UserId == user.Id)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new HistoryItem(a.Id, a.TargetTitle, a.Score, a.TailoredJson != null, a.CreatedAt))
            .ToListAsync(ct);
        await SendOkAsync(items, ct);
    }
}

// DELETE /applications/{id} — remove a single run.
public sealed class DeleteApplicationEndpoint(GapMapDbContext db, CurrentUser user) : EndpointWithoutRequest
{
    public override void Configure() { Delete("/applications/{id}"); }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }
        var id = Route<Guid>("id");
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id && a.UserId == user.Id, ct);
        if (app is null) { await SendNotFoundAsync(ct); return; }
        db.Applications.Remove(app);
        await db.SaveChangesAsync(ct);
        await SendNoContentAsync(ct);
    }
}

// DELETE /me — erase the user's personal data (PII / GDPR / UAE PDPL right to deletion).
// Removes profile, applications, skill gaps, usage events, and the user record itself.
public sealed class DeleteAccountEndpoint(GapMapDbContext db, CurrentUser user) : EndpointWithoutRequest
{
    public override void Configure() { Delete("/me"); }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (user.Id == Guid.Empty) { await SendUnauthorizedAsync(ct); return; }
        var uid = user.Id;
        await db.Applications.Where(a => a.UserId == uid).ExecuteDeleteAsync(ct);
        await db.Profiles.Where(p => p.UserId == uid).ExecuteDeleteAsync(ct);
        await db.SkillGaps.Where(g => g.UserId == uid).ExecuteDeleteAsync(ct);
        await db.UsageEvents.Where(e => e.UserId == uid).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == uid).ExecuteDeleteAsync(ct);
        await SendNoContentAsync(ct);
    }
}
