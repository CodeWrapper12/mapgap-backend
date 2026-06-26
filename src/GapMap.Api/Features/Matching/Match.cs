using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using GapMap.Api.Ai;
using GapMap.Api.Common;
using GapMap.Api.Domain;
using GapMap.Api.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GapMap.Api.Features.Matching;

// Score is computed HERE, not by the model — same classification always yields the same number.
// matched & surfaced both count as "has it"; learnable gives partial credit; real gap = 0.
public static class ScoreCalculator
{
    public static (int score, string band) Compute(IEnumerable<RequirementClassification> reqs)
    {
        decimal Weight(string i) => string.Equals(i, "Required", StringComparison.OrdinalIgnoreCase) || string.Equals(i, "Mandatory", StringComparison.OrdinalIgnoreCase) ? 1.0m : 0.4m;
        decimal Credit(string b) => (b?.ToLowerInvariant()) switch
        {
            "matched" => 1.0m,
            "surfaced" => 1.0m,
            "learnablegap" => 0.3m,
            _ => 0.0m,
        };

        decimal num = 0, den = 0;
        foreach (var r in reqs) { var w = Weight(r.Importance); num += w * Credit(r.Bucket); den += w; }
        var score = den == 0 ? 0 : (int)Math.Round(100 * num / den);
        var band = score >= 75 ? "Strong" : score >= 50 ? "Moderate" : "Weak";
        return (score, band);
    }
}

public sealed record MatchCommand(Guid UserId, string JdText) : IRequest<(Guid appId, MatchResult result)>;

public sealed class MatchHandler(GapMapDbContext db, IAiClient ai, AiOptions ai_opts)
    : IRequestHandler<MatchCommand, (Guid, MatchResult)>
{
    public async Task<(Guid, MatchResult)> Handle(MatchCommand cmd, CancellationToken ct)
    {
        var jdHash = Hash(cmd.JdText);

        // Cache: same JD already analyzed for this user → reuse, no model call.
        var cached = await db.Applications
            .FirstOrDefaultAsync(a => a.UserId == cmd.UserId && a.JdHash == jdHash, ct);
        if (cached is not null)
        {
            var r = JsonSerializer.Deserialize<MatchResult>(cached.MatchJson, AiClient.JsonOpts)!;
            return (cached.Id, r);
        }

        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == cmd.UserId, ct)
                      ?? throw new InvalidOperationException("No profile. Onboard first.");

        var chunks = SplitJd(cmd.JdText);
        var tasks = chunks.Select(chunk => 
        {
            var content = $"<profile>{profile.ProfileJson}</profile>\n<jd>{chunk}</jd>";
            return ai.CompleteJsonAsync<MatchClassification>(
                "match", ai_opts.CheapModel, Prompts.Match, content, cmd.UserId, null, ct);
        });

        var classifications = await Task.WhenAll(tasks);
        var allRequirements = classifications.SelectMany(c => c.Requirements).ToList();
        var classification = new MatchClassification(allRequirements);

        var (score, band) = ScoreCalculator.Compute(classification.Requirements);
        var result = new MatchResult(score, band, classification.Requirements);

        var targetTitle = cmd.JdText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        var rec = new ApplicationRecord
        {
            UserId = cmd.UserId, JdHash = jdHash, JdText = cmd.JdText,
            Score = score, TargetTitle = targetTitle,
            MatchJson = JsonSerializer.Serialize(result, AiClient.JsonOpts),
        };
        db.Applications.Add(rec);

        // Maintain skill_gaps for the progress story: surfaced strengths and learnable gaps.
        foreach (var req in classification.Requirements)
        {
            var status = (req.Bucket?.ToLowerInvariant()) switch
            {
                "surfaced" => "surfaced",
                "learnablegap" => "learning", // internal bookkeeping only; never shown as "still learning"
                _ => null,
            };
            if (status is null) continue;
            var gap = await db.SkillGaps.FirstOrDefaultAsync(g => g.UserId == cmd.UserId && g.Skill == req.Requirement, ct);
            if (gap is null) db.SkillGaps.Add(new SkillGap { UserId = cmd.UserId, Skill = req.Requirement, Status = status });
            else if (gap.Status != "closed") { gap.Status = status; gap.UpdatedAt = DateTime.UtcNow; }
        }

        await db.SaveChangesAsync(ct);
        return (rec.Id, result);
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Trim())));

    private static List<string> SplitJd(string jdText)
    {
        var lines = jdText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Where(l => l.Length > 10).ToList();
        if (lines.Count <= 3) return lines.Count > 0 ? lines : [jdText];
        
        var chunkSize = (int)Math.Ceiling(lines.Count / 3.0);
        return lines.Chunk(chunkSize).Select(c => string.Join("\n", c)).ToList();
    }
}

public sealed record MatchRequest(string JdText);
public sealed record MatchResponse(Guid ApplicationId, MatchResult Match);

public sealed class MatchEndpoint(ISender sender, CurrentUser user) : Endpoint<MatchRequest, MatchResponse>
{
    public override void Configure() { Post("/match"); }

    public override async Task HandleAsync(MatchRequest req, CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }
        if (req.JdText.Length > 10_000) { AddError(r => r.JdText, "That looks too long — paste just the job description."); await SendErrorsAsync(cancellation: ct); return; }

        var (appId, result) = await sender.Send(new MatchCommand(user.Id, req.JdText), ct);
        await SendOkAsync(new MatchResponse(appId, result), ct);
    }
}
