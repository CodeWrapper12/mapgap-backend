using System.Text.Json;
using FastEndpoints;
using GapMap.Api.Ai;
using GapMap.Api.Common;
using GapMap.Api.Domain;
using GapMap.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GapMap.Api.Features.Profiles;

// GET /profile — return the stored rich profile for the current user.
public sealed class GetProfileEndpoint(GapMapDbContext db, CurrentUser user) : EndpointWithoutRequest<CandidateProfile>
{
    public override void Configure() { Get("/profile"); }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }
        var rec = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        if (rec is null) { await SendNotFoundAsync(ct); return; }
        var profile = JsonSerializer.Deserialize<CandidateProfile>(rec.ProfileJson, AiClient.JsonOpts)!;
        await SendOkAsync(profile, ct);
    }
}

// PUT /profile — save user edits to the profile (the user can correct the parse).
public sealed class UpdateProfileEndpoint(GapMapDbContext db, CurrentUser user) : Endpoint<CandidateProfile>
{
    public override void Configure() { Put("/profile"); }

    public override async Task HandleAsync(CandidateProfile req, CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }
        var json = JsonSerializer.Serialize(req, AiClient.JsonOpts);
        var rec = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        if (rec is null) db.Profiles.Add(new ProfileRecord { UserId = user.Id, ProfileJson = json });
        else { rec.ProfileJson = json; rec.Version++; rec.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
        await SendOkAsync(ct);
    }
}
