using System.Text.Json;
using GapMap.Api.Infrastructure;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace GapMap.Api.Ai;

// One wrapper around every model call. It:
//  - sends a system prompt + delimited user content,
//  - forces JSON output (low temperature for determinism),
//  - records usage (tokens + computed cost) to usage_events — NOTHING bypasses this,
//  - returns the deserialized result.
//
// NOTE: Semantic Kernel's settings/usage-metadata surface changes between versions.
// The structured-output (response_format json_schema) and usage extraction below are
// written to current intent; verify against the SK + OpenAI SDK version you restore.
public interface IAiClient
{
    Task<T> CompleteJsonAsync<T>(
        string operation, string model, string systemPrompt, string userContent,
        Guid userId, Guid? applicationId, CancellationToken ct);
}

public sealed class AiClient(
    Kernel kernel, IServiceProvider provider, ModelRates rates, ILogger<AiClient> log) : IAiClient
{
    public async Task<T> CompleteJsonAsync<T>(
        string operation, string model, string systemPrompt, string userContent,
        Guid userId, Guid? applicationId, CancellationToken ct)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userContent);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            Seed = 1, // fixed seed for reproducibility; verify the property exists in your SK version
            ModelId = model,
            ResponseFormat = "json_object", // prefer a strict json_schema where the SDK supports it
        };

        var result = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);
        var text = result.Content ?? "{}";

        // Usage metadata location varies by SK version; this reads the common shape.
        var (inTok, outTok) = ExtractUsage(result.Metadata);
        var cost = rates.Cost(model, inTok, outTok);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GapMapDbContext>();
            db.UsageEvents.Add(new UsageEvent
            {
                UserId = userId, ApplicationId = applicationId, Operation = operation,
                Model = model, InputTokens = inTok, OutputTokens = outTok, CostUsd = cost,
            });
            await db.SaveChangesAsync(ct);
        }
        
        log.LogInformation("AI {Op} model={Model} in={In} out={Out} cost={Cost:F4}", operation, model, inTok, outTok, cost);

        return JsonSerializer.Deserialize<T>(text, JsonOpts)
               ?? throw new InvalidOperationException($"Model returned unparseable JSON for {operation}.");
    }

    private static (int, int) ExtractUsage(IReadOnlyDictionary<string, object?>? meta)
    {
        if (meta is null) return (0, 0);
        // OpenAI via SK typically exposes a "Usage" object with prompt/completion token counts.
        if (meta.TryGetValue("Usage", out var u) && u is not null)
        {
            var t = u.GetType();
            int p = (int?)(t.GetProperty("InputTokenCount") ?? t.GetProperty("PromptTokens"))?.GetValue(u) ?? 0;
            int c = (int?)(t.GetProperty("OutputTokenCount") ?? t.GetProperty("CompletionTokens"))?.GetValue(u) ?? 0;
            return (p, c);
        }
        return (0, 0);
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
