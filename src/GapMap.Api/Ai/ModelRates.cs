namespace GapMap.Api.Ai;

// Per-model $/1K-token rates, bound from appsettings (Ai:Rates).
// Cost is computed from these and STORED at write time, so historical numbers
// don't shift when provider pricing changes. Update rates when pricing changes;
// past UsageEvents keep the cost they were written with.
public sealed class ModelRates
{
    public Dictionary<string, Rate> Rates { get; set; } = new();

    public decimal Cost(string model, int inputTokens, int outputTokens)
    {
        if (!Rates.TryGetValue(model, out var r)) return 0m;
        return (inputTokens / 1000m * r.InputPer1K) + (outputTokens / 1000m * r.OutputPer1K);
    }
}

public sealed class Rate
{
    public decimal InputPer1K { get; set; }
    public decimal OutputPer1K { get; set; }
}

public sealed class AiOptions
{
    public string CheapModel { get; set; } = "gpt-4o-mini";
    public string StrongModel { get; set; } = "gpt-4o-mini"; // bump to a stronger model for tailor/cover
    public string ApiKey { get; set; } = "";
}

public sealed class QuotaOptions
{
    public decimal GlobalHardStopUsd { get; set; } = 2.50m;   // against the shared API balance
    public decimal GlobalSoftAlertUsd { get; set; } = 2.00m;
    public int PerUserApplicationsPerMonth { get; set; } = 10;
}
