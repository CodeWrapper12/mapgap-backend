using System.Text.Json;
using FastEndpoints;
using GapMap.Api.Ai;
using GapMap.Api.Common;
using GapMap.Api.Domain;
using GapMap.Api.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GapMap.Api.Features.Tailoring;

// The mechanical enforcer of "polish, never originate". A strict schema can make the model
// FILL a provenance field; it cannot verify the provenance is real or that a bullet didn't
// smuggle in an unsupported claim. This pass does.
public sealed class OutputValidator(GapMapDbContext db)
{
    public async Task<TailorResult> ValidateAsync(
        Guid userId, List<TailoredBullet> bullets, List<ConfirmedItem> confirmed, CancellationToken ct)
    {
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        var profileText = (profile?.ProfileJson ?? "").ToLowerInvariant();

        // The set of legitimate sources: profile ids/text + the user's typed seeds.
        var seeds = confirmed.Where(c => !string.IsNullOrWhiteSpace(c.Seed))
                             .Select(c => c.Seed!.ToLowerInvariant()).ToList();
        var refs = confirmed.Where(c => !string.IsNullOrWhiteSpace(c.EvidenceRef))
                            .Select(c => c.EvidenceRef!).ToHashSet();

        var ok = new List<TailoredBullet>();
        var rejected = new List<string>();

        foreach (var b in bullets)
        {
            // 1. Provenance required.
            if (string.IsNullOrWhiteSpace(b.Provenance)) { rejected.Add(b.Requirement); continue; }

            // 2. Provenance must resolve: either a real evidence_ref, or text traceable to a seed.
            var prov = b.Provenance;
            var resolves = refs.Contains(prov)
                           || profileText.Contains(prov.ToLowerInvariant())
                           || seeds.Any(s => s.Contains(prov.ToLowerInvariant()) || prov.ToLowerInvariant().Contains(s));
            if (!resolves) { rejected.Add(b.Requirement); continue; }

            // 3. Skill backing: every skill the bullet asserts must appear in the profile or a seed.
            //    This is the rule that stops a bullet with valid provenance from smuggling in a skill
            //    the candidate doesn't have.
            var unbackedSkill = b.SkillsUsed.FirstOrDefault(sk =>
            {
                var s = sk.ToLowerInvariant().Trim();
                return s.Length > 0 && !profileText.Contains(s) && !seeds.Any(seed => seed.Contains(s));
            });
            if (unbackedSkill is not null) { rejected.Add($"{b.Requirement} (unbacked skill: {unbackedSkill})"); continue; }

            // 4. No invented metric: any digit run in the bullet must appear in profile or a seed.
            if (HasUnbackedNumber(b.Bullet, profileText, seeds)) { rejected.Add(b.Requirement); continue; }

            ok.Add(b);
        }

        return new TailorResult(ok, rejected);
    }

    private static bool HasUnbackedNumber(string bullet, string profileText, List<string> seeds)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(bullet, @"\d[\d,\.]*%?"))
        {
            var n = m.Value;
            if (profileText.Contains(n) || seeds.Any(s => s.Contains(n))) continue;
            return true; // a number with no source
        }
        return false;
    }

    // Prose-aware scan for the cover letter. Same principle as the per-bullet check, but a letter
    // can't carry per-claim provenance — so we scan-and-flag rather than reject. Any number in the
    // letter that doesn't trace to the profile or the user's own inputs is surfaced to the user.
    public async Task<List<string>> ScanProseAsync(
        Guid userId, string letter, IEnumerable<string> userInputs, CancellationToken ct)
    {
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        var profileText = (profile?.ProfileJson ?? "").ToLowerInvariant();
        var inputs = userInputs.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.ToLowerInvariant()).ToList();

        var flags = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(letter, @"\d[\d,\.]*%?"))
        {
            var n = m.Value;
            if (profileText.Contains(n) || inputs.Any(s => s.Contains(n))) continue;
            flags.Add($"Unbacked figure \"{n}\" — verify before sending.");
        }
        return flags;
    }
}

public sealed record TailorCommand(Guid UserId, TailorRequestModel Model) : IRequest<TailorResult>;

public sealed class TailorHandler(GapMapDbContext db, IAiClient ai, AiOptions opts, OutputValidator validator)
    : IRequestHandler<TailorCommand, TailorResult>
{
    public async Task<TailorResult> Handle(TailorCommand cmd, CancellationToken ct)
    {
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == cmd.UserId, ct)
                      ?? throw new InvalidOperationException("No profile.");
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == cmd.Model.ApplicationId, ct)
                  ?? throw new InvalidOperationException("Unknown application.");

        // Drop any item with neither a real evidence_ref nor a seed before it ever reaches the model.
        var items = cmd.Model.ConfirmedItems
            .Where(c => !string.IsNullOrWhiteSpace(c.EvidenceRef) || !string.IsNullOrWhiteSpace(c.Seed))
            .ToList();

        var content = $"<profile>{profile.ProfileJson}</profile>\n" +
                      $"<jd>{app.JdText}</jd>\n" +
                      $"<confirmedItems>{JsonSerializer.Serialize(items, AiClient.JsonOpts)}</confirmedItems>";

        var raw = await ai.CompleteJsonAsync<TailorClassification>(
            "tailor", opts.StrongModel, Prompts.Tailor, content, cmd.UserId, app.Id, ct);

        var result = await validator.ValidateAsync(cmd.UserId, raw.Bullets, items, ct);

        // Persist the validated bullets so /export and /cover-letter can assemble the finalized CV.
        app.TailoredJson = JsonSerializer.Serialize(result, AiClient.JsonOpts);

        // A learnable gap that the user confirmed with a seed and that survived validation is now "closed".
        var closedSkills = result.Bullets
            .Where(bl => items.Any(i => i.Requirement == bl.Requirement && !string.IsNullOrWhiteSpace(i.Seed)))
            .Select(bl => bl.Requirement);
        foreach (var skill in closedSkills)
        {
            var gap = await db.SkillGaps.FirstOrDefaultAsync(g => g.UserId == cmd.UserId && g.Skill == skill, ct);
            if (gap is not null) { gap.Status = "closed"; gap.UpdatedAt = DateTime.UtcNow; }
        }

        await db.SaveChangesAsync(ct);
        return result;
    }
}

public sealed class TailorEndpoint(ISender sender, CurrentUser user) : Endpoint<TailorRequestModel, TailorResult>
{
    public override void Configure() { Post("/tailor"); }

    public override async Task HandleAsync(TailorRequestModel req, CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }
        var result = await sender.Send(new TailorCommand(user.Id, req), ct);
        await SendOkAsync(result, ct);
    }
}
