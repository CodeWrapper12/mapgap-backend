namespace GapMap.Api.Domain;

// ---------- Rich profile (parse output; stored as jsonb) ----------
// Every bullet/experience/project carries a stable id — these are the evidence_refs
// the match and validator resolve against. The id thread is the honesty mechanism.

public sealed record CandidateProfile(
    Identity Identity,
    string? SummarySeed,
    Skills Skills,
    List<Experience> Experience,
    List<ProjectItem> Projects,
    List<Education> Education,
    List<Certification> Certifications,
    Seniority? Seniority);

public sealed record Identity(string Name, List<string> TargetTitles, Contact Contact);
public sealed record Contact(string? Email, string? Phone, List<string> Links);

public sealed record Skills(
    List<string> Languages, List<string> Backend, List<string> Architecture, List<string> Data,
    List<string> Cloud, List<string> Frontend, List<string> Auth, List<string> Ai,
    List<string> Observability, List<string> Practices, List<string> Tools,
    List<string> Testing, List<string> DevOps, List<string> Mobile, List<string> Security);

public sealed record Experience(
    string Id, string Company, string Title, string? Location,
    string? Start, string? End, bool Current, string? Description, List<Bullet> Bullets);

public sealed record Bullet(string Id, string Text, List<string> Metrics, List<string> Tech);

public sealed record ProjectItem(string Id, string Name, string? Period, string? Description, string? Link, List<string> Tech);
public sealed record Education(string Degree, string Institution, string? Location, string? Start, string? End, List<string>? Highlights);
public sealed record Certification(string Name, string? Issuer, string? Date);
public sealed record Seniority(string? TotalYearsOfExperience, string? Level, List<string> Domains);

// ---------- Match (classification output; score computed in code) ----------

public sealed record RequirementClassification(
    string Requirement,
    string? SearchAnalysis,
    string Importance,
    string Bucket,
    string? EvidenceRef,        // required for Matched & Surfaced
    string? JdPhrase,           // Surfaced
    string? ProfileEvidence,    // Surfaced
    string? Rationale,          // LearnableGap
    string? SuggestedLearnPath, // LearnableGap
    string? Impact);            // RealGap

// What the model returns (no score — we compute it).
public sealed record MatchClassification(List<RequirementClassification> Requirements);

// What we persist and return to the client.
public sealed record MatchResult(
    int Score, string ScoreBand, List<RequirementClassification> Requirements);

// ---------- Tailoring ----------

// source is exactly one of EvidenceRef (a profile id) or Seed (user-typed truth).
public sealed record ConfirmedItem(string Requirement, string? EvidenceRef, string? Seed);

public sealed record TailorRequestModel(Guid ApplicationId, List<ConfirmedItem> ConfirmedItems);

public sealed record TailoredBullet(string Requirement, string Bullet, string Provenance, List<string> SkillsUsed);
public sealed record TailorClassification(List<TailoredBullet> Bullets);
public sealed record TailorResult(List<TailoredBullet> Bullets, List<string> Rejected);

// ---------- Cover letter (optional) ----------
public sealed record CoverLetterInputs(string? Motivation, string? HiringManager, string? Tone, string? Length);
public sealed record CoverLetterRequestModel(Guid ApplicationId, List<string> SelectedPoints, CoverLetterInputs Inputs);

// Model returns letter + the evidence it drew on; we scan and return any unbacked claims as flags.
public sealed record CoverLetterModelResult(string Letter, List<string> EvidenceUsed);
public sealed record CoverLetterResult(string Letter, List<string> Flags);
