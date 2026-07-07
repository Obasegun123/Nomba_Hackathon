namespace Nomba_Hackathon.Models;

public class ReconciliationException
{
    public Guid Id { get; set; }

    // Reference to the transaction that caused the exception
    public string TransactionRef { get; set; } = string.Empty;

    // Type of exception: "webhook_loss", "amount_mismatch", "double_settlement", "api_error", etc
    public string ExceptionType { get; set; } = string.Empty;

    // Human-readable error message
    public string ErrorMessage { get; set; } = string.Empty;

    // LLM diagnosis of what went wrong
    public string? AiDiagnosis { get; set; }

    // LLM recommendation for remediation
    public string? AiRecommendation { get; set; }

    // Confidence score from LLM (0-1)
    public decimal? AiConfidence { get; set; }

    // Exception status: "PENDING", "APPROVED", "REJECTED", "RESOLVED"
    public string Status { get; set; } = "PENDING";

    // Who approved/rejected this exception (operator email or system)
    public string? ApprovedBy { get; set; }

    // What action was taken to resolve
    public string? ResolutionAction { get; set; }

    // When the exception was created
    public DateTimeOffset CreatedAt { get; set; }

    // When the exception was resolved
    public DateTimeOffset? ResolvedAt { get; set; }

    // Raw context data (JSON) for debugging
    public string? ContextData { get; set; }
}
