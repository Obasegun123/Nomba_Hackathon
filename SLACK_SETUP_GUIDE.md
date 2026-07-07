# Slack Notifications Setup Guide

Get real-time alerts in Slack when reconciliation exceptions are detected.

## 5-Minute Setup

### Step 1: Create a Slack App

1. Go to [Slack App Directory](https://api.slack.com/apps)
2. Click **"Create New App"**
3. Select **"From scratch"**
4. **App Name**: `Nomba Reconciliation Bot`
5. **Workspace**: Select your workspace
6. Click **"Create App"**

### Step 2: Enable Incoming Webhooks

1. In the app settings, go to **"Incoming Webhooks"** (left sidebar)
2. Toggle **"Activate Incoming Webhooks"** to ON
3. Click **"Add New Webhook to Workspace"**
4. **Select a channel** where alerts should post (e.g., `#reconciliation-alerts` or `#nomba`)
5. Click **"Allow"** to authorize
6. **Copy the Webhook URL** — it looks like:
   ```
   https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXX
   ```

### Step 3: Configure Your App

Add the webhook URL to your app configuration:

**Option A: Using .NET User Secrets (Recommended for Development)**
```bash
cd c:\Users\SegunObasooto\source\repos\API\Nomba_Hackathon
dotnet user-secrets set "Slack:WebhookUrl" "https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXX"
```

**Option B: Environment Variable**
```bash
# Windows PowerShell
$env:Slack__WebhookUrl = "https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXX"
```

**Option C: appsettings.json (Development Only)**
```json
{
  "Slack": {
    "WebhookUrl": "https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXX"
  }
}
```

### Step 4: Restart Your App

```bash
dotnet run
```

The Slack service auto-registers when the webhook URL is configured. No code changes needed.

---

## Test It

1. Trigger a reconciliation exception manually (or wait for the next 5-min sweep)
2. Check your Slack channel — you should see:

```
⚠️ Reconciliation Exception Detected

Transaction Reference
test-ref-123

Exception Type
webhook_loss

Error Message
Transaction succeeded upstream but local ledger not settled

AI Diagnosis
The transaction succeeded upstream (₦10,000) but the webhook was lost...

AI Recommendation
Settle with upstream amount (₦10,000)... (Confidence: 98%)
```

---

## Troubleshooting

### No messages appearing?

1. **Check webhook URL**: Verify the URL is correctly set in secrets
   ```bash
   dotnet user-secrets list
   ```
   
2. **Check app logs**: Run with verbose logging:
   ```bash
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   dotnet run
   ```
   Look for: `Slack notification sent for exception`

3. **Verify channel permissions**: Make sure the bot has permission to post in the channel

4. **Test webhook manually**:
   ```bash
   curl -X POST -H 'Content-type: application/json' \
     --data '{"text":"Test message"}' \
     "https://hooks.slack.com/services/..."
   ```

### Messages are delayed?

- Exceptions are sent asynchronously; there may be a 1-2 second delay
- This is normal and intentional (doesn't block reconciliation)

### Want to change the channel?

1. Go to [Slack Apps](https://api.slack.com/apps)
2. Select your app
3. Go to **"Incoming Webhooks"**
4. Delete the old webhook
5. Add a new one to the desired channel
6. Update your configuration with the new URL

---

## Advanced: Customize Messages

To customize Slack message appearance (colors, fields, buttons), edit:
```
Service/SlackNotificationService.cs → BuildSlackMessage()
```

Current message includes:
- ✅ Transaction reference
- ✅ Exception type (color-coded)
- ✅ Error message
- ✅ AI diagnosis (if available)
- ✅ AI recommendation + confidence (if available)
- ✅ Links to view details / API docs
- ✅ Timestamp

---

## Production Considerations

1. **Rate Limiting**: Slack allows ~1 message/second per webhook. With reconciliation every 5 min, you're well within limits.

2. **Sensitive Data**: Webhook URLs are secrets—never commit them to version control. Always use `dotnet user-secrets` or environment variables.

3. **Notification Fatigue**: High-volume exceptions could spam the channel. Consider:
   - Batching exceptions (hourly digest)
   - Only alerting on high-confidence issues (`aiConfidence > 0.90`)
   - Filtering by exception type (e.g., only critical types)

4. **Channel Strategy**:
   - `#reconciliation-alerts` — all exceptions
   - `#nomba-critical` — double-settlement & critical errors only
   - `@oncall` — high-confidence + high-risk exceptions

---

## Slack Message API Reference

If you want to extend or customize messages, check:
- [Slack Block Kit](https://api.slack.com/block-kit) — message layout language
- [Slack Blocks](https://api.slack.com/block-kit/building) — available blocks
- [Slack Message Payloads](https://api.slack.com/messaging/webhooks) — webhook format

Our implementation uses `blocks` format for rich, interactive messages.
