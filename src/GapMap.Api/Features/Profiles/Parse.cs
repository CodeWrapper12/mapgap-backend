using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using FastEndpoints;
using GapMap.Api.Ai;
using GapMap.Api.Common;
using GapMap.Api.Domain;
using GapMap.Api.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GapMap.Api.Features.Profiles;

// File -> plain text happens BEFORE the model (never feed a binary to the LLM).
// Both PDF and docx supported; other types rejected.
public static class CvTextExtractor
{
    public static string Extract(string fileName, Stream stream)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => FromPdf(stream),
            ".docx" => FromDocx(stream),
            _ => throw new NotSupportedException(
                $"Unsupported file type '{ext}'. Please upload a PDF (.pdf) or Word (.docx) file."),
        };
    }

    private static string FromPdf(Stream s)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(s);
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
        }
        return sb.ToString();
    }

    private static string FromDocx(Stream s)
    {
        using var doc = WordprocessingDocument.Open(s, false);
        var body = doc.MainDocumentPart?.Document.Body;
        if (body is null) return "";

        var sb = new StringBuilder();
        foreach (var p in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            sb.AppendLine(p.InnerText);
        }
        return sb.ToString();
    }
}

public sealed record ParseCommand(Guid UserId, string? FileName, byte[]? FileBytes, string Answers) : IRequest<CandidateProfile>;

public sealed class ParseHandler(GapMapDbContext db, IAiClient ai, AiOptions opts)
    : IRequestHandler<ParseCommand, CandidateProfile>
{
    public async Task<CandidateProfile> Handle(ParseCommand cmd, CancellationToken ct)
    {
        string cvText = "";
        if (cmd.FileBytes is not null && cmd.FileName is not null)
        {
            try
            {
                using var ms = new MemoryStream(cmd.FileBytes);
                cvText = CvTextExtractor.Extract(cmd.FileName, ms);
            }
            catch (Exception ex) when (ex is not NotSupportedException)
            {
                // Catch PdfPig or OpenXml corrupted file exceptions
                throw new FluentValidation.ValidationException(
                    "The uploaded file could not be read or is corrupted. Please ensure it is a valid PDF or Word document.");
            }
            if (cvText.Length > 40_000) cvText = cvText[..40_000]; // cap a huge CV before the model sees it
        }

        var content = $"<cv>{cvText}</cv>\n<answers>{cmd.Answers}</answers>";
        var profile = await ai.CompleteJsonAsync<CandidateProfile>(
            "parse", opts.CheapModel, Prompts.Parse, content, cmd.UserId, null, ct);

        var json = JsonSerializer.Serialize(profile, AiClient.JsonOpts);

        // Concurrency handling for Upsert
        var saved = false;
        var retries = 0;
        while (!saved && retries < 2)
        {
            try
            {
                var existing = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == cmd.UserId, ct);
                if (existing is null) db.Profiles.Add(new ProfileRecord { UserId = cmd.UserId, ProfileJson = json });
                else { existing.ProfileJson = json; existing.Version++; existing.UpdatedAt = DateTime.UtcNow; }
                
                await db.SaveChangesAsync(ct);
                saved = true;
            }
            catch (DbUpdateException)
            {
                retries++;
                // Clear the tracker to retry
                db.ChangeTracker.Clear();
                if (retries >= 2) throw;
            }
        }

        return profile;
    }
}

public sealed class ParseEndpoint(ISender sender, CurrentUser user)
    : EndpointWithoutRequest
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    public override void Configure() { Post("/profile/parse"); AllowFileUploads(); }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!user.IsApproved) { await SendForbiddenAsync(ct); return; }

        var file = Files.Count > 0 ? Files[0] : null;
        var answers = Form["answers"].ToString();

        byte[]? fileBytes = null;
        string? fileName = null;

        if (file is not null)
        {
            // Reject files larger than 10 MB to prevent memory exhaustion.
            if (file.Length > MaxFileSizeBytes)
            {
                AddError($"File is too large ({file.Length / (1024 * 1024):F1} MB). Maximum allowed size is 10 MB.");
                await SendErrorsAsync(cancellation: ct);
                return;
            }

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".pdf" && ext != ".docx")
            {
                AddError($"Unsupported file type '{ext}'. Please upload a PDF (.pdf) or Word (.docx) file.");
                await SendErrorsAsync(cancellation: ct);
                return;
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            fileBytes = ms.ToArray();
            fileName = file.FileName;
        }

        try
        {
            var profile = await sender.Send(new ParseCommand(user.Id, fileName, fileBytes, answers), ct);
            await SendOkAsync(profile, ct);
        }
        catch (FluentValidation.ValidationException ex)
        {
            AddError(ex.Message);
            await SendErrorsAsync(cancellation: ct);
        }
    }
}
