using System.Text.Json;
using FastEndpoints;
using GapMap.Api.Ai;
using GapMap.Api.Common;
using GapMap.Api.Domain;
using GapMap.Api.Features.Tailoring;
using GapMap.Api.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GapMap.Api.Features.CoverLetter;

public sealed record CoverLetterCommand(Guid UserId, CoverLetterRequestModel Model) : IRequest<CoverLetterResult>;

public sealed class CoverLetterHandler(GapMapDbContext db, IAiClient ai, AiOptions opts, OutputValidator validator)
    : IRequestHandler<CoverLetterCommand, CoverLetterResult>
{
    public async Task<CoverLetterResult> Handle(CoverLetterCommand cmd, CancellationToken ct)
    {
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == cmd.UserId, ct)
                      ?? throw new InvalidOperationException("No profile.");
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == cmd.Model.ApplicationId, ct)
                  ?? throw new InvalidOperationException("Unknown application.");

        var m = cmd.Model;
        var content =
            $"<profile>{profile.ProfileJson}</profile>\n" +
            $"<jd>{app.JdText}</jd>\n" +
            $"<selectedPoints>{JsonSerializer.Serialize(m.SelectedPoints)}</selectedPoints>\n" +
            $"<inputs>{JsonSerializer.Serialize(m.Inputs, AiClient.JsonOpts)}</inputs>";

        var result = await ai.CompleteJsonAsync<CoverLetterModelResult>(
            "cover_letter", opts.StrongModel, Prompts.CoverLetter, content, cmd.UserId, app.Id, ct);

        // Prose-aware honesty scan: flag any figure that doesn't trace to the profile or the user's inputs.
        var userInputs = new[] { m.Inputs.Motivation ?? "", m.Inputs.HiringManager ?? "" };
        var flags = await validator.ScanProseAsync(cmd.UserId, result.Letter, userInputs, ct);

        return new CoverLetterResult(result.Letter, flags);
    }
}

public sealed class CoverLetterEndpoint(ISender sender, CurrentUser user)
    : Endpoint<CoverLetterRequestModel, CoverLetterResult>
{
    public override void Configure() { Post("/cover-letter"); }

    public override async Task HandleAsync(CoverLetterRequestModel req, CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }
        var result = await sender.Send(new CoverLetterCommand(user.Id, req), ct);
        await SendOkAsync(result, ct);
    }
}
