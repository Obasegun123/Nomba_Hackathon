using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nomba_Hackathon.Service;

/// <summary>
/// Qwen LLM provider implementation. Uses Qwen API for reconciliation exception analysis.
/// API key is configured via .NET secrets (LLM:QwenApiKey).
/// </summary>
public class QwenLlmProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<QwenLlmProvider> _logger;
    private const string QwenBaseUrl = "https://dashscope.aliyuncs.com/api/v1/services/aigc/text-generation/generation";
    private const string QwenModel = "qwen-max";

    public QwenLlmProvider(HttpClient httpClient, IConfiguration config, ILogger<QwenLlmProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = config["LLM:QwenApiKey"] ?? throw new InvalidOperationException("LLM:QwenApiKey not configured");
    }

    public async Task<LlmAnalysisResult> AnalyzeExceptionAsync(ExceptionContext context)
    {
        try
        {
            var prompt = BuildAnalysisPrompt(context);
            var response = await CallQwenApiAsync(prompt);
            return ParseQwenResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Qwen LLM analysis failed for transaction {Ref}", context.TransactionRef);
            // Return a safe fallback
            return new LlmAnalysisResult
            {
                Diagnosis = "LLM analysis unavailable",
                Recommendation = "Manual review required",
                Confidence = 0,
                Risks = new() { "LLM service error" }
            };
        }
    }

    private string BuildAnalysisPrompt(ExceptionContext context)
    {
        return $@"You are an expert Nigerian fintech auditor analyzing a payment reconciliation issue.

TRANSACTION DETAILS:
- Reference: {context.TransactionRef}
- Amount: ₦{context.Amount}
- Account: {context.AccountId}
- Created: {context.CreatedAt:O}
- Current Status: {context.Status}
- Exception Type: {context.ExceptionType}

UPSTREAM STATE (Nomba API):
- Status: {context.UpstreamStatus ?? "Unknown"}
- Amount: {context.UpstreamAmount?.ToString() ?? "Unknown"}
- Timestamp: {context.UpstreamTimestamp?.ToString("O") ?? "Unknown"}
- Error: {context.UpstreamError ?? "None"}

LOCAL LEDGER STATE:
- Current Balance: ₦{context.CurrentBalance}
- Ledger Entries Posted: {context.LedgerEntryCount}
- Last Update: {context.LastLedgerUpdate?.ToString("O") ?? "Never"}

ERROR MESSAGE: {context.ErrorMessage}

ADDITIONAL LOGS:
Webhook: {context.WebhookLog ?? "None"}
API Error: {context.ApiErrorLog ?? "None"}

ANALYSIS REQUIRED:
1. Diagnose what went wrong (why is there a divergence?)
2. Recommend the next action (settle, reverse, investigate further?)
3. Rate your confidence (0-1)
4. List any risks with the recommendation

RESPOND IN JSON FORMAT ONLY:
{{
  ""diagnosis"": ""Clear explanation of what happened"",
  ""recommendation"": ""Specific action to take (e.g., 'Settle with upstream amount of ₦X')"",
  ""confidence"": 0.95,
  ""risks"": [""risk1"", ""risk2""]
}}";
    }

    private async Task<string> CallQwenApiAsync(string prompt)
    {
        var request = new QwenRequest
        {
            Model = QwenModel,
            Input = new QwenInput { Messages = new[] { new QwenMessage { Role = "user", Content = prompt } } },
            Parameters = new QwenParameters { Temperature = 0.3m }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            System.Text.Encoding.UTF8,
            "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

        var response = await _httpClient.PostAsync(QwenBaseUrl, content);
        response.EnsureSuccessStatusCode();

        var responseText = await response.Content.ReadAsStringAsync();
        var qwenResponse = JsonSerializer.Deserialize<QwenResponse>(responseText);

        if (qwenResponse?.Output?.Choices == null || qwenResponse.Output.Choices.Length == 0)
            throw new InvalidOperationException("Empty response from Qwen API");

        return qwenResponse.Output.Choices[0].Message?.Content ?? "";
    }

    private LlmAnalysisResult ParseQwenResponse(string responseText)
    {
        try
        {
            // Extract JSON from the response (Qwen sometimes wraps it)
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart == -1 || jsonEnd == -1)
                throw new InvalidOperationException("No JSON found in response");

            var jsonStr = responseText.Substring(jsonStart, jsonEnd - jsonStart + 1);
            var parsed = JsonSerializer.Deserialize<QwenAnalysisResponse>(jsonStr);

            if (parsed == null)
                throw new InvalidOperationException("Failed to parse analysis response");

            return new LlmAnalysisResult
            {
                Diagnosis = parsed.Diagnosis ?? "Unknown",
                Recommendation = parsed.Recommendation ?? "Manual review required",
                Confidence = parsed.Confidence,
                Risks = parsed.Risks ?? new()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Qwen response: {Response}", responseText);
            return new LlmAnalysisResult
            {
                Diagnosis = "Parse error",
                Recommendation = "Manual review required",
                Confidence = 0,
                Risks = new() { "Response parsing failed" }
            };
        }
    }

    #region Qwen API DTOs

    private record QwenRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("input")]
        public QwenInput Input { get; set; } = new();

        [JsonPropertyName("parameters")]
        public QwenParameters Parameters { get; set; } = new();
    }

    private record QwenInput
    {
        [JsonPropertyName("messages")]
        public QwenMessage[] Messages { get; set; } = Array.Empty<QwenMessage>();
    }

    private record QwenMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private record QwenParameters
    {
        [JsonPropertyName("temperature")]
        public decimal Temperature { get; set; } = 0.3m;
    }

    private record QwenResponse
    {
        [JsonPropertyName("output")]
        public QwenOutput? Output { get; set; }

        [JsonPropertyName("usage")]
        public QwenUsage? Usage { get; set; }
    }

    private record QwenOutput
    {
        [JsonPropertyName("choices")]
        public QwenChoice[]? Choices { get; set; }
    }

    private record QwenChoice
    {
        [JsonPropertyName("message")]
        public QwenMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private record QwenUsage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }
    }

    private record QwenAnalysisResponse
    {
        [JsonPropertyName("diagnosis")]
        public string? Diagnosis { get; set; }

        [JsonPropertyName("recommendation")]
        public string? Recommendation { get; set; }

        [JsonPropertyName("confidence")]
        public decimal Confidence { get; set; }

        [JsonPropertyName("risks")]
        public List<string>? Risks { get; set; }
    }

    #endregion
}
