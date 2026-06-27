using System.Text;
using FastEndpoints;
using GapMap.Api.Ai;
using GapMap.Api.Common;
using GapMap.Api.Features.Auth;
using GapMap.Api.Features.Tailoring;
using GapMap.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.SemanticKernel;
using FastEndpoints.Swagger;
using Scalar.AspNetCore;
using System.IdentityModel.Tokens.Jwt;

JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---- Options ----
var jwtOpts = cfg.GetSection("Jwt").Get<JwtOptions>() ?? new();
var aiOpts = cfg.GetSection("Ai").Get<AiOptions>() ?? new();
var quotaOpts = cfg.GetSection("Quota").Get<QuotaOptions>() ?? new();
var rates = new ModelRates { Rates = cfg.GetSection("Ai:Rates").Get<Dictionary<string, Rate>>() ?? new() };

// ---- JWT secret guard: fail fast if placeholder or too short ----
const string placeholder = "REPLACE_WITH_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARS";
if (string.IsNullOrWhiteSpace(jwtOpts.Key) || jwtOpts.Key == placeholder || jwtOpts.Key.Length < 32)
    throw new InvalidOperationException(
        "JWT signing key is missing or still set to the placeholder value. " +
        "Set a secure random key (≥ 32 chars) via Jwt:Key in appsettings, user-secrets, or an environment variable.");

builder.Services.AddSingleton(jwtOpts);
builder.Services.AddSingleton(aiOpts);
builder.Services.AddSingleton(quotaOpts);
builder.Services.AddSingleton(rates);
var allowedOrigin = cfg["Frontend:Origin"] ?? "http://localhost:3000";
Console.WriteLine($"DEBUG: CORS Allowed Origin is: {allowedOrigin}");

// ---- Data ----
builder.Services.AddDbContext<GapMapDbContext>(o =>
    o.UseNpgsql(cfg.GetConnectionString("Postgres")));

// ---- Semantic Kernel (two model tiers via one kernel; model chosen per call) ----
builder.Services.AddSingleton(_ =>
{
    var kb = Kernel.CreateBuilder();
    kb.AddOpenAIChatCompletion(aiOpts.CheapModel, aiOpts.ApiKey); // model overridden per request
    return kb.Build();
});

// ---- App services ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<IAiClient, AiClient>();
builder.Services.AddScoped<OutputValidator>();
builder.Services.AddMediatR(c => c.RegisterServicesFromAssembly(typeof(Program).Assembly));
builder.Services.AddFastEndpoints();
builder.Services.SwaggerDocument(o =>
{
    o.DocumentSettings = s =>
    {
        s.Title = "GapMap API";
        s.Version = "v1";
    };
});

// ---- Auth (JWT bearer; status/role live in claims) ----
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwtOpts.Issuer,
            ValidateAudience = true, ValidAudience = jwtOpts.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.Key)),
            ValidateLifetime = true,
        };
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Admin", p => p.RequireClaim("role", "admin"));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(cfg["Frontend:Origin"] ?? "http://localhost:3000").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Seed admin user and default voucher on startup (idempotent).
await DbSeeder.SeedAsync(app.Services);

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<ErrorLoggingMiddleware>();
app.UseMiddleware<QuotaMiddleware>(); // after auth so CurrentUser is populated
app.UseAuthorization();
app.UseFastEndpoints(c =>
{
    c.Endpoints.RoutePrefix = "api";
    c.Serializer.Options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

app.UseOpenApi();
app.MapScalarApiReference(options =>
{
    options.WithOpenApiRoutePattern("/swagger/{documentName}/swagger.json");
});

app.Run();
