# LLM-Powered Reconciliation Exception Analysis

## Overview

Your reconciliation engine now includes automated exception detection and AI-powered root cause analysis. When the reconciliation sweep encounters ambiguous cases (webhook loss, amount mismatches, double-settlement risks, API errors), it:

1. **Logs the exception** to the `reconciliation_exceptions` table
2. **Triggers LLM analysis** (if configured) to diagnose the issue
3. **Proposes a remediation action** with confidence scoring
4. **Waits for human approval** before executing the fix

---

## Architecture

### Exception Detection

The `ReconciliationService` now detects four categories of exceptions:

- **webhook_loss**: Transaction converged successfully upstream but local ledger hadn't received the webhook
- **amount_mismatch**: Local expected amount differs from upstream settled amount
- **double_settlement**: Ledger already has entries posted; attempting to settle again would double-credit
- **api_error**: Failed to query the upstream provider
- **unexpected_status**: Upstream returned a status code the system doesn't recognize

### LLM Analysis Pipeline

```
Exception Detected
    ↓
Build Context (transaction state, ledger state, logs)
    ↓
Call ILlmProvider.AnalyzeExceptionAsync()
    ↓
Get Diagnosis + Recommendation + Confidence
    ↓
Store in reconciliation_exceptions table
    ↓
Operator reviews via /exceptions endpoints
    ↓
Human approves → Execute remediation
```

---

## Configuration

### Enable Qwen LLM Analysis

Add your Qwen API key to your .NET secrets:

```bash
# Set the secret (one-time)
dotnet user-secrets set "LLM:QwenApiKey" "your-qwen-api-key" --project Nomba_Hackathon.csproj
```

Or in `appsettings.Development.json` (development only):

```json
{
  "LLM": {
    "QwenApiKey": "your-qwen-api-key"
  }
}
```

Once configured, the `QwenLlmProvider` automatically registers and analyzes exceptions.

### Enable Slack Notifications

1. **Create a Slack App** (if you don't have one):
   - Go to https://api.slack.com/apps
   - Click "Create New App"
   - Choose "From scratch"
   - Name it (e.g., "Nomba Reconciliation Bot")
   - Select your workspace

2. **Enable Incoming Webhooks**:
   - In your app settings, go to "Incoming Webhooks"
   - Toggle "Activate Incoming Webhooks" to ON
   - Click "Add New Webhook to Workspace"
   - Select a channel (e.g., #reconciliation-alerts)
   - Authorize

3. **Copy the Webhook URL** (looks like `https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXX`)

4. **Add to .NET Secrets**:
```bash
dotnet user-secrets set "Slack:WebhookUrl" "https://hooks.slack.com/services/T00.../..." --project Nomba_Hackathon.csproj
```

Or in `appsettings.Development.json`:

```json
{
  "Slack": {
    "WebhookUrl": "https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXX"
  }
}
```

Once configured, exceptions automatically trigger Slack messages with:
- Exception type and error message
- LLM diagnosis + confidence (if available)
- Direct links to review in the API

### Running Without Slack (Optional)

If you don't configure `Slack:WebhookUrl`, exceptions are still logged and can be reviewed via `/exceptions` endpoints. Slack is purely optional for real-time alerts.

---

## Slack Message Format

When an exception is detected, you'll receive a Slack message like this:

```
⚠️ Reconciliation Exception Detected

Transaction Reference
order-12345

Exception Type
amount_mismatch

Error Message
Local: ₦5000, Upstream: ₦5500

AI Diagnosis
Customer paid more than the expected amount; this is an overpayment scenario.

AI Recommendation
Settle with upstream amount (₦5500) and record the ₦500 overpayment credit. (Confidence: 94%)

[View Details] [API Docs]

Timestamp: 2024-07-05T10:30:00Z
```

**Exception Types & Colors**:
- 🟠 **webhook_loss** — Orange (requires settlement)
- 🔴 **amount_mismatch** — Red (may indicate overpayment/underpayment)
- 🔴 **double_settlement** — Crimson (high-risk, manual review)
- 🟠 **api_error** — Red-Orange (transient vs permanent?)
- 🟡 **unexpected_status** — Gold (unknown upstream status)

Click "View Details" to open the exceptions dashboard and approve/reject the exception.

---

## API Endpoints

### List Exceptions

```http
GET /exceptions?status=PENDING&page=1&pageSize=50
Authorization: X-API-Key [your-api-key]
```

Response:
```json
{
  "total": 3,
  "page": 1,
  "pageSize": 50,
  "exceptions": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "transactionRef": "order-12345",
      "exceptionType": "amount_mismatch",
      "errorMessage": "Local: ₦5000, Upstream: ₦5500",
      "aiDiagnosis": "Customer paid more than the expected amount; this is an overpayment scenario.",
      "aiRecommendation": "Settle with upstream amount (₦5500) and record the ₦500 overpayment credit.",
      "aiConfidence": 0.94,
      "status": "PENDING",
      "createdAt": "2024-07-05T10:30:00Z"
    }
  ]
}
```

### Get Exception Details

```http
GET /exceptions/{id}
Authorization: X-API-Key [your-api-key]
```

### Approve Exception

```http
PATCH /exceptions/{id}/approve
Authorization: X-API-Key [your-api-key]
```

If the recommendation is a settlement action, the system will execute `PaymentService.SettleAsync()` automatically.

Response:
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": "APPROVED",
  "action": "Executed settlement"
}
```

### Reject Exception

```http
PATCH /exceptions/{id}/reject
Authorization: X-API-Key [your-api-key]
```

---

## Example: Webhook Loss Detection

**Scenario**: A webhook never arrived, but reconciliation polling found the transaction succeeded upstream.

**What happens**:

1. `ReconciliationService` queries Nomba API → gets `SUCCESS` status
2. Checks local ledger → no entries posted yet
3. Detects: "This looks like a webhook loss; the upstream says SUCCESS but we haven't settled locally"
4. Logs exception with type `webhook_loss`
5. **LLM Analysis** (if enabled):
   - Diagnosis: "The transaction succeeded upstream (₦10,000 credited) but the settlement webhook was lost, leaving the local ledger out of sync."
   - Recommendation: "Settle with upstream amount (₦10,000) to converge the ledger."
   - Confidence: 0.98
6. Operator reviews → clicks `/approve` → system calls `SettleAsync()` → ledger entries posted
7. Exception status → `RESOLVED`

---

## Extending with Other LLM Providers

The system is designed for multi-provider support. To add Claude, OpenAI, or another provider:

1. **Create a new implementation of `ILlmProvider`**:
   ```csharp
   public class ClaudeLlmProvider : ILlmProvider
   {
       public async Task<LlmAnalysisResult> AnalyzeExceptionAsync(ExceptionContext context)
       {
           // Call Claude API, parse response, return result
       }
   }
   ```

2. **Register in `Program.cs`**:
   ```csharp
   if (!string.IsNullOrEmpty(config["LLM:ClaudeApiKey"]))
   {
       builder.Services.AddHttpClient<ClaudeLlmProvider>();
       builder.Services.AddScoped<ILlmProvider, ClaudeLlmProvider>();
   }
   ```

3. **Add configuration**:
   ```bash
   dotnet user-secrets set "LLM:ClaudeApiKey" "your-claude-key"
   ```

Only one provider is active at a time (the first one registered).

---

## Monitoring

The `/metrics` endpoint now includes reconciliation exception stats:

```http
GET /metrics
Authorization: X-API-Key [your-api-key]
```

In a future iteration, you could expose:
- `reconciliation.exceptions.pending` (count of unresolved exceptions)
- `reconciliation.exceptions.resolved_last_24h` (operational metric)
- `reconciliation.ai_confidence_avg` (LLM reliability signal)

---

## Database Schema

### reconciliation_exceptions Table

```sql
CREATE TABLE reconciliation_exceptions (
    id                UUID PRIMARY KEY,
    transaction_ref   VARCHAR(100),       -- Link to the transaction that triggered the exception
    exception_type    VARCHAR(50),        -- Type: webhook_loss, amount_mismatch, etc
    error_message     VARCHAR(500),       -- Human-readable error
    ai_diagnosis      TEXT,               -- LLM diagnosis (if available)
    ai_recommendation TEXT,               -- LLM recommendation
    ai_confidence     DECIMAL(3,2),       -- LLM confidence (0-1)
    status            VARCHAR(20),        -- PENDING, APPROVED, REJECTED, RESOLVED
    approved_by       VARCHAR(255),       -- Operator who approved
    resolution_action TEXT,               -- What action was executed
    created_at        TIMESTAMP,          -- When the exception was logged
    resolved_at       TIMESTAMP,          -- When it was resolved
    context_data      JSONB              -- Raw transaction/ledger state for audit
);
```

---

## Testing

### Manual Test: Trigger an Exception

1. Create a payment with amount ₦5,000
2. Simulate an upstream response of ₦5,500 (amount mismatch)
3. Reconciliation sweep runs → detects mismatch
4. LLM analyzes → proposes settlement with ₦5,500
5. Check `/exceptions?status=PENDING` → see the exception with AI diagnosis
6. Approve → system settles with the correct amount

### Integration Test: LLM Provider Failure

If the LLM API is down:
- Exception is still logged (no diagnosis)
- Operator can manually review and approve
- System degrades gracefully (no crash)

---

## Production Considerations

1. **LLM Cost**: Each exception triggers an API call. Monitor usage to avoid surprise bills.
2. **Confidence Thresholds**: In production, you might auto-approve exceptions with `aiConfidence > 0.95` and only show low-confidence ones to operators.
3. **Human-in-the-Loop**: Never auto-execute critical fixes (reversals, large settlements) without operator approval.
4. **Audit Trail**: The `context_data` JSONB column stores the full transaction/ledger state for compliance and debugging.
5. **Rate Limiting**: Reconciliation runs every 5 minutes; with 100 transactions per run, you could hit API rate limits. Consider batching or throttling LLM calls.

---

## Next Steps

- [ ] Build a dashboard to visualize pending exceptions and operator actions
- [ ] Add auto-approval for high-confidence, low-risk exceptions
- [ ] Integrate with Slack for exception notifications
- [ ] Add support for additional LLM providers (Claude, OpenAI, Ollama)
- [ ] Create operational metrics for TTR (time-to-resolution) and exception rates
