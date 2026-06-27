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
using WDocument = DocumentFormat.OpenXml.Wordprocessing.Document;
using WColor = DocumentFormat.OpenXml.Wordprocessing.Color;
using WPageSize = DocumentFormat.OpenXml.Wordprocessing.PageSize;
using PDocument = QuestPDF.Fluent.Document;

namespace GapMap.Api.Features.Export;

// The finalized CV model: one source of truth, two renderers. Both must parse cleanly.
// This is the exact thing the original CV got wrong — so bullets are native list items
// (docx) / single flowed lines (pdf), never literal glyphs in their own frame.
//
// Skills keep their category labels (so they render "Languages: …" like the real CV),
// and the model now carries Projects / Education / Certifications — these were being
// dropped by the assembler before, which is why the export could never match the CV.
public sealed record CvModel(
    string Name,
    string TargetTitle,
    string Contact,
    string Summary,
    List<CvSkillGroup> SkillGroups,
    List<CvExperience> Experience,
    List<CvProject> Projects,
    List<CvEducation> Education,
    List<CvCertification> Certifications);

public sealed record CvSkillGroup(string Label, List<string> Items);
public sealed record CvExperience(string Company, string Role, string Location, string Period, List<string> Bullets);
public sealed record CvProject(string Name, string Period, string Description, List<string> Tech);
public sealed record CvEducation(string Degree, string Institution, string Period);
public sealed record CvCertification(string Name, string Issuer);

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
            var period = $"{e.Start} – {(e.Current ? "Present" : e.End)}";
            exps.Add(new CvExperience(e.Company, e.Title, e.Location ?? "", period, bullets));
        }

        var contact = string.Join("  ·  ",
            new[] { p.Identity.Contact.Email, p.Identity.Contact.Phone }
            .Concat(p.Identity.Contact.Links ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)));

        var title = app.TargetTitle ?? p.Identity.TargetTitles.FirstOrDefault() ?? "";

        var projects = (p.Projects ?? new())
            .Select(pr => new CvProject(pr.Name, pr.Period ?? "", pr.Description ?? "", pr.Tech ?? new()))
            .ToList();

        var education = (p.Education ?? new())
            .Select(ed => new CvEducation(ed.Degree, ed.Institution, Range(ed.Start, ed.End)))
            .ToList();

        var certs = (p.Certifications ?? new())
            .Select(c => new CvCertification(c.Name, c.Issuer ?? ""))
            .ToList();

        return new CvModel(
            p.Identity.Name, title, contact, p.SummarySeed ?? "",
            BuildSkills(p.Skills), exps, projects, education, certs);
    }

    // Preserve the category labels instead of flattening everything into one list.
    static List<CvSkillGroup> BuildSkills(Skills s)
    {
        var groups = new List<CvSkillGroup>();
        void Add(string label, List<string>? items)
        {
            var v = (items ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (v.Count > 0) groups.Add(new CvSkillGroup(label, v));
        }
        Add("Languages", s.Languages);
        Add("Backend & Frameworks", s.Backend);
        Add("Architecture & Patterns", s.Architecture);
        Add("Databases & Data Access", s.Data);
        Add("Cloud & DevOps", s.Cloud);
        Add("Frontend", s.Frontend);
        Add("Auth & Security", s.Auth);
        Add("AI", s.Ai);
        Add("Observability & Testing", s.Observability);
        Add("Practices", s.Practices);
        return groups;
    }

    static string Range(string? a, string? b)
        => string.Join(" – ", new[] { a, b }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static class DocxRenderer
{
    public static byte[] Render(CvModel cv)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new WDocument(new Body());
            var body = main.Document.Body!;

            // ---- Numbering definition for native bullets ----
            // The previous version referenced numId=1 but never created this part, so Word
            // had nothing to resolve the list against. This is the fix for broken docx bullets.
            var numPart = main.AddNewPart<NumberingDefinitionsPart>();
            numPart.Numbering = new Numbering(
                new AbstractNum(
                    new Level(
                        new NumberingFormat { Val = NumberFormatValues.Bullet },
                        new LevelText { Val = "•" },
                        new ParagraphProperties(new Indentation { Left = "720", Hanging = "360" })
                    ) { LevelIndex = 0 }
                ) { AbstractNumberId = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });

            const int rightTab = 9740; // A4, 0.75" margins → usable ≈ 9746 twips

            // Single section, single column. No tables, no text boxes, no frames.
            void Para(string text, bool bold = false, int size = 20, string? color = null)
            {
                var rPr = new RunProperties(
                    new Bold { Val = OnOffValue.FromBoolean(bold) },
                    new FontSize { Val = size.ToString() });
                if (color != null) rPr.Append(new WColor { Val = color });
                body.Append(new Paragraph(new Run(rPr, new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
            }

            // Uppercase section heading with a hairline rule beneath (purely visual; ATS-safe).
            void Section(string title)
            {
                var pPr = new ParagraphProperties(
                    new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 4U, Space = 1U, Color = "BBBBBB" }),
                    new SpacingBetweenLines { Before = "160", After = "40" });
                body.Append(new Paragraph(pPr,
                    new Run(new RunProperties(new Bold(), new FontSize { Val = "22" }, new WColor { Val = "333333" }),
                        new Text(title.ToUpperInvariant()) { Space = SpaceProcessingModeValues.Preserve })));
            }

            // Header line: bold title (+ optional lighter trailing text) on the left, dates on the right.
            // Right-alignment via a right tab stop — reads in order, no table involved.
            void HeaderLine(string leftBold, string? leftNormal, string right)
            {
                var p = new Paragraph(new ParagraphProperties(
                    new Tabs(new TabStop { Val = TabStopValues.Right, Position = rightTab })));
                p.Append(new Run(new RunProperties(new Bold(), new FontSize { Val = "20" }),
                    new Text(leftBold) { Space = SpaceProcessingModeValues.Preserve }));
                if (!string.IsNullOrWhiteSpace(leftNormal))
                    p.Append(new Run(new RunProperties(new FontSize { Val = "20" }, new WColor { Val = "555555" }),
                        new Text(leftNormal) { Space = SpaceProcessingModeValues.Preserve }));
                p.Append(new Run(new TabChar()));
                p.Append(new Run(new RunProperties(new FontSize { Val = "18" }, new WColor { Val = "555555" }),
                    new Text(right ?? "") { Space = SpaceProcessingModeValues.Preserve }));
                body.Append(p);
            }

            void Bullet(string text)
                => body.Append(new Paragraph(
                    new ParagraphProperties(new NumberingProperties(
                        new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
                    new Run(new RunProperties(new FontSize { Val = "20" }),
                        new Text(text) { Space = SpaceProcessingModeValues.Preserve })));

            // ---- header ----
            Para(cv.Name, bold: true, size: 36);
            if (!string.IsNullOrWhiteSpace(cv.TargetTitle)) Para(cv.TargetTitle, size: 24, color: "444444");
            if (!string.IsNullOrWhiteSpace(cv.Contact)) Para(cv.Contact, size: 18, color: "555555");

            // ---- summary ----
            if (!string.IsNullOrWhiteSpace(cv.Summary)) { Section("Summary"); Para(cv.Summary); }

            // ---- skills (categorized: "Label: items") ----
            if (cv.SkillGroups.Count > 0)
            {
                Section("Skills");
                foreach (var g in cv.SkillGroups)
                    body.Append(new Paragraph(
                        new Run(new RunProperties(new Bold(), new FontSize { Val = "20" }),
                            new Text($"{g.Label}: ") { Space = SpaceProcessingModeValues.Preserve }),
                        new Run(new RunProperties(new FontSize { Val = "20" }),
                            new Text(string.Join(", ", g.Items)) { Space = SpaceProcessingModeValues.Preserve })));
            }

            // ---- experience ----
            if (cv.Experience.Count > 0)
            {
                Section("Work Experience");
                foreach (var e in cv.Experience)
                {
                    var loc = string.IsNullOrWhiteSpace(e.Location) ? null : $"  |  {e.Location}";
                    HeaderLine($"{e.Company} — {e.Role}", loc, e.Period);
                    foreach (var b in e.Bullets) Bullet(b);
                }
            }

            // ---- projects ----
            if (cv.Projects.Count > 0)
            {
                Section("Projects");
                foreach (var pr in cv.Projects)
                {
                    HeaderLine(pr.Name, null, pr.Period);
                    if (!string.IsNullOrWhiteSpace(pr.Description)) Para(pr.Description);
                    if (pr.Tech.Count > 0)
                        body.Append(new Paragraph(new Run(
                            new RunProperties(new Italic(), new FontSize { Val = "18" }, new WColor { Val = "555555" }),
                            new Text($"Tech: {string.Join(", ", pr.Tech)}") { Space = SpaceProcessingModeValues.Preserve })));
                }
            }

            // ---- education ----
            if (cv.Education.Count > 0)
            {
                Section("Education");
                foreach (var ed in cv.Education)
                {
                    var inst = string.IsNullOrWhiteSpace(ed.Institution) ? null : $" — {ed.Institution}";
                    HeaderLine(ed.Degree, inst, ed.Period);
                }
            }

            // ---- certifications ----
            if (cv.Certifications.Count > 0)
            {
                Section("Certifications");
                foreach (var c in cv.Certifications)
                    Bullet(string.IsNullOrWhiteSpace(c.Issuer) ? c.Name : $"{c.Name} — {c.Issuer}");
            }

            // Page setup (A4, 0.75" margins) so the right tab stop lands at the margin.
            body.Append(new SectionProperties(
                new WPageSize { Width = 11906U, Height = 16838U },
                new PageMargin { Top = 1080, Right = 1080U, Bottom = 1080, Left = 1080U, Header = 720U, Footer = 720U, Gutter = 0U }));

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
        return PDocument.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Margin(36); // 0.5"
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Black)); // selectable text, single column
                page.Content().Column(col =>
                {
                    col.Spacing(3);

                    // Uppercase heading + hairline rule.
                    void Section(string title)
                    {
                        col.Item().PaddingTop(8).Text(title.ToUpperInvariant())
                            .FontSize(11).Bold().FontColor(Colors.Grey.Darken3);
                        col.Item().PaddingTop(1).LineHorizontal(0.75f).LineColor(Colors.Grey.Lighten1);
                    }

                    // Bullet via a Row: glyph then text, inline, single reading order — extracts as "• text".
                    // The RelativeItem gives wrapped lines a proper hanging indent.
                    void Bullet(string s) => col.Item().Row(r =>
                    {
                        r.ConstantItem(12).Text("•");
                        r.RelativeItem().Text(s);
                    });

                    // ---- header ----
                    col.Item().Text(cv.Name).FontSize(18).Bold();
                    if (!string.IsNullOrWhiteSpace(cv.TargetTitle))
                        col.Item().Text(cv.TargetTitle).FontSize(12).FontColor(Colors.Grey.Darken2);
                    if (!string.IsNullOrWhiteSpace(cv.Contact))
                        col.Item().PaddingTop(2).Text(cv.Contact).FontSize(9).FontColor(Colors.Grey.Darken1);

                    // ---- summary ----
                    if (!string.IsNullOrWhiteSpace(cv.Summary))
                    {
                        Section("Summary");
                        col.Item().PaddingTop(2).Text(cv.Summary);
                    }

                    // ---- skills (categorized) ----
                    if (cv.SkillGroups.Count > 0)
                    {
                        Section("Skills");
                        col.Item().PaddingTop(2);
                        foreach (var g in cv.SkillGroups)
                            col.Item().Text(t =>
                            {
                                t.Span($"{g.Label}: ").Bold();
                                t.Span(string.Join(", ", g.Items));
                            });
                    }

                    // ---- experience ----
                    if (cv.Experience.Count > 0)
                    {
                        Section("Work Experience");
                        foreach (var e in cv.Experience)
                        {
                            col.Item().PaddingTop(8).Row(r =>
                            {
                                r.RelativeItem().Text(t =>
                                {
                                    t.Span($"{e.Company} — {e.Role}").Bold();
                                    if (!string.IsNullOrWhiteSpace(e.Location))
                                        t.Span($"  |  {e.Location}").FontColor(Colors.Grey.Darken1);
                                });
                                r.AutoItem().Text(e.Period).FontSize(9).FontColor(Colors.Grey.Darken1);
                            });
                            foreach (var b in e.Bullets) Bullet(b);
                        }
                    }

                    // ---- projects ----
                    if (cv.Projects.Count > 0)
                    {
                        Section("Projects");
                        foreach (var pr in cv.Projects)
                        {
                            col.Item().PaddingTop(6).Row(r =>
                            {
                                r.RelativeItem().Text(pr.Name).Bold();
                                if (!string.IsNullOrWhiteSpace(pr.Period))
                                    r.AutoItem().Text(pr.Period).FontSize(9).FontColor(Colors.Grey.Darken1);
                            });
                            if (!string.IsNullOrWhiteSpace(pr.Description)) col.Item().Text(pr.Description);
                            if (pr.Tech.Count > 0)
                                col.Item().Text($"Tech: {string.Join(", ", pr.Tech)}")
                                    .FontSize(9).Italic().FontColor(Colors.Grey.Darken1);
                        }
                    }

                    // ---- education ----
                    if (cv.Education.Count > 0)
                    {
                        Section("Education");
                        foreach (var ed in cv.Education)
                            col.Item().PaddingTop(6).Row(r =>
                            {
                                r.RelativeItem().Text(t =>
                                {
                                    t.Span(ed.Degree).Bold();
                                    if (!string.IsNullOrWhiteSpace(ed.Institution)) t.Span($" — {ed.Institution}");
                                });
                                if (!string.IsNullOrWhiteSpace(ed.Period))
                                    r.AutoItem().Text(ed.Period).FontSize(9).FontColor(Colors.Grey.Darken1);
                            });
                    }

                    // ---- certifications ----
                    if (cv.Certifications.Count > 0)
                    {
                        Section("Certifications");
                        col.Item().PaddingTop(2);
                        foreach (var c in cv.Certifications)
                            Bullet(string.IsNullOrWhiteSpace(c.Issuer) ? c.Name : $"{c.Name} — {c.Issuer}");
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

        // round-trip on the new skills shape (flatten group items) + the name
        var atsTerms = cv.SkillGroups.SelectMany(g => g.Items).Concat([cv.Name]);
        if (!AtsSelfCheck.Passes(bytes, ext, atsTerms))
            ThrowError("Generated file failed the ATS round-trip check."); // a generation bug, not the user's problem

        await SendBytesAsync(bytes, $"cv{ext}", contentType, cancellation: ct);
    }
}