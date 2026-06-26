using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FastEndpoints;
using GapMap.Api.Ai;
using GapMap.Api.Common;
using GapMap.Api.Domain;
using GapMap.Api.Infrastructure;
using GapMap.Api.Features.Profiles; // CvTextExtractor for the round-trip check
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GapMap.Api.Features.Export;

// The finalized CV model: one source of truth, two renderers. Both must parse cleanly.
// This is the exact thing the original CV got wrong — so bullets are native list items
// (docx) / single flowed paragraphs (pdf), never literal glyphs in their own frame.
public sealed record CvModel(string Name, string TargetTitle, string Contact, string Summary,
    List<string> Skills, List<CvExperience> Experience);
public sealed record CvExperience(string Company, string Role, string Period, List<string> Bullets);

// Assembles the finalized CV from the stored profile + the validated tailored bullets.
// Tailored bullets attach to the experience entry their provenance resolves to (by id prefix);
// seed-sourced bullets attach to the current/most-recent role. Entries with no tailored bullet
// keep their original profile text — so export still works even before /tailor has run.
public static class CvAssembler
{
    public static CvModel Build(CandidateProfile p, ApplicationRecord app)
    {
        var tailored = string.IsNullOrWhiteSpace(app.TailoredJson)
            ? new TailorResult(new(), new())
            : JsonSerializer.Deserialize<TailorResult>(app.TailoredJson, AiClient.JsonOpts)!;

        var skills = new[] { p.Skills.Languages, p.Skills.Backend, p.Skills.Architecture, p.Skills.Data,
                p.Skills.Cloud, p.Skills.Frontend, p.Skills.Auth, p.Skills.Ai, p.Skills.Observability, p.Skills.Practices }
            .SelectMany(x => x ?? new()).Distinct().ToList();

        var exps = new List<CvExperience>();
        var currentIdx = p.Experience.FindIndex(e => e.Current);
        if (currentIdx < 0) currentIdx = 0;

        for (var i = 0; i < p.Experience.Count; i++)
        {
            var e = p.Experience[i];
            // tailored bullets whose provenance points at this entry (e.g. "exp_1" or "exp_1_b2")
            var mine = tailored.Bullets
                .Where(b => b.Provenance == e.Id || b.Provenance.StartsWith(e.Id + "_", StringComparison.Ordinal))
                .Select(b => b.Bullet).ToList();

            // seed-sourced tailored bullets (provenance is not any experience id) → current role
            if (i == currentIdx)
            {
                var allExpIds = p.Experience.Select(x => x.Id).ToHashSet();
                var seedBullets = tailored.Bullets
                    .Where(b => !allExpIds.Any(id => b.Provenance == id || b.Provenance.StartsWith(id + "_", StringComparison.Ordinal)))
                    .Select(b => b.Bullet);
                mine.AddRange(seedBullets);
            }

            var bullets = mine.Count > 0 ? mine : (e.Bullets?.Select(b => b.Text).ToList() ?? new());
            var period = $"{e.Start} — {(e.Current ? "Present" : e.End)}";
            exps.Add(new CvExperience(e.Company, e.Title, period, bullets));
        }

        var contact = string.Join("  ·  ",
            new[] { p.Identity.Contact.Email, p.Identity.Contact.Phone }
            .Concat(p.Identity.Contact.Links ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)));

        var title = app.TargetTitle ?? p.Identity.TargetTitles.FirstOrDefault() ?? "";
        return new CvModel(p.Identity.Name, title, contact, p.SummarySeed ?? "", skills, exps);
    }
}

public static class DocxRenderer
{
    public static byte[] Render(CvModel cv)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new Body());
            var body = main.Document.Body!;

            // Single section, single column. No tables, no text boxes, no frames.
            void Para(string text, bool bold = false, int size = 22)
                => body.Append(new Paragraph(new Run(
                    new RunProperties(new Bold { Val = OnOffValue.FromBoolean(bold) }, new FontSize { Val = size.ToString() }),
                    new Text(text) { Space = SpaceProcessingModeValues.Preserve })));

            Para(cv.Name, bold: true, size: 32);
            if (!string.IsNullOrWhiteSpace(cv.TargetTitle)) Para(cv.TargetTitle, size: 24);
            if (!string.IsNullOrWhiteSpace(cv.Contact)) Para(cv.Contact, size: 18);
            Para("");
            if (!string.IsNullOrWhiteSpace(cv.Summary)) { Para("Summary", bold: true); Para(cv.Summary); Para(""); }
            if (cv.Skills.Count > 0) { Para("Skills", bold: true); Para(string.Join("  •  ", cv.Skills)); Para(""); }
            Para("Experience", bold: true);
            foreach (var e in cv.Experience)
            {
                Para($"{e.Company} — {e.Role}", bold: true);
                Para(e.Period, size: 18);
                foreach (var b in e.Bullets)
                    // Native list paragraph (numbering definition), not a literal "•" in its own frame.
                    body.Append(new Paragraph(
                        new ParagraphProperties(new NumberingProperties(
                            new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
                        new Run(new Text(b) { Space = SpaceProcessingModeValues.Preserve })));
                Para("");
            }
            main.Document.Save();
        }
        return ms.ToArray();
    }
}

public static class PdfRenderer
{
    public static byte[] Render(CvModel cv)
    {
        QuestPDF.Settings.License = LicenseType.Community; // verify your eligibility
        return QuestPDF.Fluent.Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Margin(40);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black)); // selectable text, single column
                page.Content().Column(col =>
                {
                    col.Spacing(6);
                    col.Item().Text(cv.Name).FontSize(18).Bold();
                    if (!string.IsNullOrWhiteSpace(cv.TargetTitle)) col.Item().Text(cv.TargetTitle).FontSize(12);
                    if (!string.IsNullOrWhiteSpace(cv.Contact)) col.Item().Text(cv.Contact).FontSize(9).FontColor(Colors.Grey.Darken2);
                    if (!string.IsNullOrWhiteSpace(cv.Summary)) { col.Item().PaddingTop(8).Text("Summary").Bold(); col.Item().Text(cv.Summary); }
                    if (cv.Skills.Count > 0) { col.Item().PaddingTop(6).Text("Skills").Bold(); col.Item().Text(string.Join("  •  ", cv.Skills)); }
                    col.Item().PaddingTop(6).Text("Experience").Bold();
                    foreach (var e in cv.Experience)
                    {
                        col.Item().PaddingTop(4).Text($"{e.Company} — {e.Role}").Bold();
                        col.Item().Text(e.Period).FontSize(9).FontColor(Colors.Grey.Darken1);
                        foreach (var b in e.Bullets)
                            // marker + text in one flowed line so a parser keeps them together
                            col.Item().Text($"•  {b}");
                    }
                });
            });
        }).GeneratePdf();
    }
}

// ATS round-trip self-check: re-parse the file we just produced and assert content survives.
public static class AtsSelfCheck
{
    public static bool Passes(byte[] bytes, string ext, IEnumerable<string> mustContain)
    {
        using var ms = new MemoryStream(bytes);
        var text = CvTextExtractor.Extract($"x{ext}", ms);
        return mustContain.Where(s => !string.IsNullOrWhiteSpace(s))
            .All(s => text.Contains(s.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record ExportRequest(Guid ApplicationId, List<string> Formats);

public sealed class ExportEndpoint(GapMapDbContext db, CurrentUser user) : Endpoint<ExportRequest>
{
    public override void Configure() { Post("/export"); }

    public override async Task HandleAsync(ExportRequest req, CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }

        var profileRec = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == req.ApplicationId && a.UserId == user.Id, ct);
        if (profileRec is null || app is null) { await SendNotFoundAsync(ct); return; }

        var profile = JsonSerializer.Deserialize<CandidateProfile>(profileRec.ProfileJson, AiClient.JsonOpts)!;
        var cv = CvAssembler.Build(profile, app);

        var fmt = req.Formats.FirstOrDefault() ?? "pdf";
        var (bytes, contentType, ext) = fmt == "docx"
            ? (DocxRenderer.Render(cv), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx")
            : (PdfRenderer.Render(cv), "application/pdf", ".pdf");

        if (!AtsSelfCheck.Passes(bytes, ext, cv.Skills.Concat([cv.Name])))
            ThrowError("Generated file failed the ATS round-trip check."); // a generation bug, not the user's problem

        await SendBytesAsync(bytes, $"cv{ext}", contentType, cancellation: ct);
    }
}
