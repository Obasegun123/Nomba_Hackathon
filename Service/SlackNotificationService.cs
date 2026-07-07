using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nomba_Hackathon.Service;

/// <summary>
/// Sends reconciliation exception notifications to Slack.
/// Configured via Slack:WebhookUrl in .NET secrets or appsettings.
/// </summary>
public interface ISlackNotificationService
{
    Task NotifyExceptionDetectedAsync(
        string transactionRef,
        string exceptionType,
        string errorMessage,
        string? aiDiagnosis = null,
        string? aiRecommendation = null,
        decimal? aiConfidence = null);
}

public class SlackNotificationService : ISlackNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;
    private readonly ILogger<SlackNotificationService> _logger;

    public SlackNotificationService(
        HttpClient httpClient,
        IConfiguration config,
        ILogger<SlackNotificationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _webhookUrl = config["Slack:WebhookUrl"] ?? throw new InvalidOperationException("Slack:WebhookUrl not configured");
    }

    public async Task NotifyExceptionDetectedAsync(
        string transactionRef,
        string exceptionType,
        string errorMessage,
        string? aiDiagnosis = null,
        string? aiRecommendation = null,
        decimal? aiConfidence = null)
    {
        try
        {
            var payload = BuildSlackMessage(
                transactionRef, exceptionType, errorMessage, aiDiagnosis, aiRecommendation, aiConfidence);

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(_webhookUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Slack notification failed for {Ref}: {StatusCode} {Body}",
                    transactionRef, response.StatusCode, responseBody);
            }
            else
            {
                _logger.LogInformation("Slack notification sent for exception {Ref}", transactionRef);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Slack notification for {Ref}", transactionRef);
        }
    }

    private object BuildSlackMessage(
        string transactionRef,
        string exceptionType,
        string errorMessage,
        string? aiDiagnosis,
        string? aiRecommendation,
        decimal? aiConfidence)
    {
        var color = GetColorForExceptionType(exceptionType);
        var fields = new List<object>
        {
            new
            {
                type = "mrkdwn",
                text = $"*Transaction Reference:*\n`{transactionRef}`"
            },
            new
            {
                type = "mrkdwn",
                text = $"*Exception Type:*\n`{exceptionType}`"
            },
            new
            {
                type = "mrkdwn",
                text = $"*Error Message:*\n{errorMessage}"
            }
        };

        if (!string.IsNullOrEmpty(aiDiagnosis))
        {
            fields.Add(new
            {
                type = "mrkdwn",
                text = $"*AI Diagnosis:*\n{aiDiagnosis}"
            });
        }

        if (!string.IsNullOrEmpty(aiRecommendation))
        {
            var confidenceStr = aiConfidence.HasValue
                ? $" (Confidence: {aiConfidence:P0})"
                : "";
            fields.Add(new
            {
                type = "mrkdwn",
                text = $"*AI Recommendation:*\n{aiRecommendation}{confidenceStr}"
            });
        }

        return new
        {
            blocks = new object[]
            {
                new
                {
                    type = "header",
                    text = new { type = "plain_text", text = "⚠️ Reconciliation Exception Detected" }
                },
                new
                {
                    type = "section",
                    fields = fields
                },
                new
                {
                    type = "actions",
                    elements = new object[]
                    {
                        new
                        {
                            type = "button",
                            text = new { type = "plain_text", text = "View Details" },
                            url = $"http://localhost:5000/exceptions?filter={transactionRef}",
                            style = "primary"
                        },
                        new
                        {
                            type = "button",
                            text = new { type = "plain_text", text = "API Docs" },
                            url = "http://localhost:5000/swagger",
                            style = "danger"
                        }
                    }
                },
                new
                {
                    type = "context",
                    elements = new object[]
                    {
                        new { type = "mrkdwn", text = $"_Timestamp: {DateTimeOffset.UtcNow:O}_" }
                    }
                }
            }
        };
    }

    private string GetColorForExceptionType(string exceptionType)
    {
        return exceptionType switch
        {
            "webhook_loss" => "#FFA500",      // Orange
            "amount_mismatch" => "#FF6B6B",   // Red
            "double_settlement" => "#DC143C", // Crimson
            "api_error" => "#FF4500",         // Red-Orange
            "unexpected_status" => "#FFD700", // Gold
            _ => "#808080"                    // Gray
        };
    }
}
