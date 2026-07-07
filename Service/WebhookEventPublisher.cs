using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;
using Nomba_Hackathon.Models;

namespace Nomba_Hackathon.Service;

public class WebhookEventPublisher
{
    private readonly LedgerDbContext _db;
    private readonly HttpClient _http;
    private readonly ILogger<WebhookEventPublisher> _logger;
    private const int MaxRetries = 5;

    public WebhookEventPublisher(LedgerDbContext db, HttpClient http, ILogger<WebhookEventPublisher> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task PublishAsync(
        string eventType,
        string accountRef,
        string? customerId,
        object payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload);

        var webhookEvent = new WebhookEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            AccountRef = accountRef,
            CustomerId = customerId,
            PayloadJson = payloadJson,
            Status = "PENDING",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.WebhookEvents.Add(webhookEvent);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Published webhook event {EventType} for account {Account} (ID: {EventId})",
            eventType, accountRef, webhookEvent.Id);

        await DeliverEventAsync(webhookEvent);
    }

    public async Task RetryPendingEventsAsync()
    {
        var pendingEvents = await _db.WebhookEvents
            .Where(e => e.Status == "PENDING" && e.RetryCount < MaxRetries)
            .OrderBy(e => e.CreatedAt)
            .Take(50)
            .ToListAsync();

        foreach (var evt in pendingEvents)
        {
            await DeliverEventAsync(evt);
        }

        var failedEvents = await _db.WebhookEvents
            .Where(e => e.Status == "PENDING" && e.RetryCount >= MaxRetries)
            .ToListAsync();

        foreach (var evt in failedEvents)
        {
            evt.Status = "FAILED";
            _logger.LogError(
                "Webhook event {EventId} ({Type}) failed after {Retries} retries",
                evt.Id, evt.EventType, evt.RetryCount);
        }

        if (failedEvents.Count > 0)
            await _db.SaveChangesAsync();
    }

    private async Task DeliverEventAsync(WebhookEvent webhookEvent)
    {
        var subscriptions = await _db.WebhookSubscriptions
            .Where(s => s.Status == "ACTIVE")
            .ToListAsync();

        foreach (var subscription in subscriptions)
        {
            await DeliverToSubscriptionAsync(webhookEvent, subscription);
        }
    }

    private async Task DeliverToSubscriptionAsync(WebhookEvent webhookEvent, WebhookSubscription subscription)
    {
        try
        {
            var signature = ComputeSignature(webhookEvent.PayloadJson ?? "", subscription.Secret ?? "");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
            {
                Content = new StringContent(webhookEvent.PayloadJson ?? "{}", Encoding.UTF8, "application/json")
            };

            request.Headers.Add("X-Webhook-Event", webhookEvent.EventType);
            request.Headers.Add("X-Webhook-Signature", signature);
            request.Headers.Add("X-Webhook-Timestamp", timestamp.ToString());
            request.Headers.Add("X-Event-ID", webhookEvent.Id.ToString());

            var response = await _http.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                webhookEvent.Status = "DELIVERED";
                webhookEvent.DeliveredAt = DateTimeOffset.UtcNow;
                subscription.SuccessCount++;
                subscription.LastDeliveryAt = DateTimeOffset.UtcNow;

                _logger.LogInformation(
                    "Webhook event {EventId} ({Type}) delivered to {Url}",
                    webhookEvent.Id, webhookEvent.EventType, subscription.Url);
            }
            else
            {
                webhookEvent.RetryCount++;
                webhookEvent.LastError = $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
                subscription.FailureCount++;

                _logger.LogWarning(
                    "Webhook delivery failed for {EventId}: {Status} (retry {Retry}/{Max})",
                    webhookEvent.Id, (int)response.StatusCode, webhookEvent.RetryCount, MaxRetries);
            }
        }
        catch (Exception ex)
        {
            webhookEvent.RetryCount++;
            webhookEvent.LastError = ex.Message;
            subscription.FailureCount++;

            _logger.LogWarning(ex,
                "Webhook delivery exception for {EventId}: {Message} (retry {Retry}/{Max})",
                webhookEvent.Id, ex.Message, webhookEvent.RetryCount, MaxRetries);
        }

        await _db.SaveChangesAsync();
    }

    private string ComputeSignature(string payload, string secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }
}
