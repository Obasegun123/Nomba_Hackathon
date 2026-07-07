namespace Nomba_Hackathon.Service;

/// <summary>
/// Abstraction for LLM-based exception analysis. Supports multiple providers
/// (Qwen, Claude, OpenAI, etc.) via configurable API keys.
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// Analyzes a reconciliation exception and returns diagnosis + recommendation.
    /// </summary>
    Task<LlmAnalysisResult> AnalyzeExceptionAsync(ExceptionContext context);
}

public record ExceptionContext
{
    public string TransactionRef { get; set; } = string.Empty;
    public string ExceptionType { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    // Transaction state
    public decimal Amount { get; set; }
    public string? AccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;

    // Upstream state (from Nomba)
    public string? UpstreamStatus { get; set; }
    public decimal? UpstreamAmount { get; set; }
    public DateTimeOffset? UpstreamTimestamp { get; set; }
    public string? UpstreamError { get; set; }

    // Ledger state
    public decimal CurrentBalance { get; set; }
    public int LedgerEntryCount { get; set; }
    public DateTimeOffset? LastLedgerUpdate { get; set; }

    // Additional context
    public string? WebhookLog { get; set; }
    public string? ApiErrorLog { get; set; }
}

public record LlmAnalysisResult
{
    public string Diagnosis { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public decimal Confidence { get; set; }  // 0-1
    public List<string> Risks { get; set; } = new();
}
