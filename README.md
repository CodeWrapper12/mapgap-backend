# GapMap — Backend (.NET 10)

A clean modular monolith with vertical slices. Each feature folder owns its endpoint(s),
handler, and logic; MediatR sequences the work; FastEndpoints exposes it; EF Core + Postgres
persists; Semantic Kernel calls the models. See the three design docs for the why behind every
decision; this README is just how to run it.

> Honesty note from your collaborator: this is a real, structured foundation, **not a verified
> running build**. It was written without a .NET toolchain or network to compile/test against.
> Expect to pin package versions and adjust a few Semantic Kernel / OpenAI SDK specifics — those
> APIs move fast. The architecture, contracts, prompts, scoring, and validator are the durable parts.

## What's implemented
- **Data model** — all six tables (`users`, `vouchers`, `profiles`, `applications`, `skill_gaps`, `usage_events`) with jsonb columns. `applications` now also stores `target_title` and the validated `tailored_json`.
- **Auth** — Google sign-in → JWT (status/role claims), voucher-gated signup, admin approval, admin endpoints.
- **Onboarding** — `/profile/parse`: PDF/docx → text (capped) → rich profile (parse-once); `GET`/`PUT /profile`.
- **Matching** — `/match`: JD → classification → **code-computed deterministic score** + band, JD-hash cache, target-title capture, `skill_gaps` upsert.
- **Tailoring** — `/tailor`: confirmed items → bullets → **output validator** (provenance, **skill-backing**, no invented numbers); validated output **persisted** to the application; closes confirmed learnable gaps.
- **Cover letter** — `/cover-letter`: optional; JD + profile + selected points → letter → prose-aware honesty scan.
- **Export** — `/export`: assembles the **real CV from the persisted tailored bullets + profile**, renders ATS-safe **docx (OpenXML)** and **PDF (QuestPDF)**, with the round-trip self-check.
- **History & deletion** — `GET /applications` (history/progress); `DELETE /applications/{id}`; `DELETE /me` (full PII erase).
- **Metering** — every model call writes a `usage_events` row with computed-and-stored cost; `temperature 0` + fixed seed.
- **Quota** — global hard stop + soft-alert log against the shared balance, plus per-user monthly cap.

## TODO / extension points (clearly marked in code)
- Swap `StrongModel` to an actual stronger model for tailor/cover once you choose one.
- Prefer a strict `json_schema` response format over `json_object` where the SDK version supports it.
- Verify the FastEndpoints file-upload API (`Files` / form access in `Parse.cs`) against your FE version.
- Verify the Semantic Kernel usage-metadata shape in `AiClient.ExtractUsage` and the `Seed` setting against your SK version.
- The CV summary uses the profile's `summary_seed` as-is; tailor the summary too if you want it role-specific.

## Setup
1. **Toolchain:** .NET 10 SDK, PostgreSQL 14+.
2. **Secrets** (don't commit): 
   ```
   dotnet user-secrets set "Ai:ApiKey" "sk-..."
   dotnet user-secrets set "Jwt:Key" "<32+ char random>"
   dotnet user-secrets set "ConnectionStrings:Postgres" "Host=...;Database=gapmap;Username=...;Password=..."
   ```
3. **Restore & migrate:**
   ```
   dotnet restore
   dotnet tool install --global dotnet-ef
   dotnet ef migrations add Initial
   dotnet ef database update
   ```
4. **Seed yourself as admin:** create one user row (or sign in once, then in the DB set your `Role = 1` (Admin) and `Status = 1` (Approved)). Create a voucher via `/api/admin/vouchers` for the next person.
5. **Run:** `dotnet run --project src/GapMap.Api` → API at `/api/...`.

## Network note
The model calls reach OpenAI and Google's token endpoint; make sure egress to both is allowed in
your environment. Quota's global hard stop is the backstop against the shared API balance draining —
keep it on.

## Endpoint map
| Method | Route | Gate |
|---|---|---|
| POST | `/api/auth/google` | anon (voucher on signup) |
| POST | `/api/profile/parse` | approved + quota |
| GET  | `/api/profile` | approved |
| PUT  | `/api/profile` | approved |
| POST | `/api/match` | approved + quota |
| POST | `/api/tailor` | approved + quota + validator |
| POST | `/api/cover-letter` | approved + quota + prose scan |
| POST | `/api/export` | approved |
| GET  | `/api/applications` | approved (history) |
| DELETE | `/api/applications/{id}` | approved |
| DELETE | `/api/me` | authenticated (PII erase) |
| GET  | `/api/admin/users` | admin |
| POST | `/api/admin/users/approve` | admin |
| POST | `/api/admin/vouchers` | admin |
| GET  | `/api/admin/usage` | admin |
