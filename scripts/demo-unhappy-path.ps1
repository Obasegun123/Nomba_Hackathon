<#
.SYNOPSIS
    NexusLedger "unhappy path" demo. Proves the resilience guarantees:
      1. Invalid webhook signature        -> 401 Unauthorized
      2. Valid webhook                     -> 200 accepted (settles the ledger)
      3. Duplicate webhook (same event)    -> duplicate_ignored (NOT processed twice)
      4. Retried X-Idempotency-Key         -> second call does NOT re-charge Nomba

.PARAMETER BaseUrl
    The running API base URL. Default: http://localhost:5292

.PARAMETER WebhookSecret
    The Nomba webhook signing secret. Defaults to the NOMBA_WEBHOOK_SECRET env var.
    (Pass it in rather than hard-coding so it never lands in source control.)

.PARAMETER ApiKey
    Optional X-Api-Key for protected endpoints. Defaults to the NEXUS_API_KEY env var.

.EXAMPLE
    $env:NOMBA_WEBHOOK_SECRET = "<your-secret>"; ./scripts/demo-unhappy-path.ps1

.NOTES
    Requires PowerShell 7+ (uses -SkipHttpErrorCheck). Scenario 4 calls the live
    Nomba sandbox and therefore needs valid Nomba credentials in user-secrets.
#>
param(
    [string]$BaseUrl = "http://localhost:5292",
    [string]$WebhookSecret = $env:NOMBA_WEBHOOK_SECRET,
    [string]$ApiKey = $env:NEXUS_API_KEY,
    [string]$AccountRef = "test-adeola-002"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($WebhookSecret)) {
    Write-Host 'ERROR: webhook secret not supplied. Set $env:NOMBA_WEBHOOK_SECRET or pass -WebhookSecret.' -ForegroundColor Red
    exit 1
}

# Nomba signs by concatenating specific fields from the payload — NOT the raw body.
# https://developer.nomba.com/docs/api-basics/webhook
# Format: event_type:request_id:user_id:wallet_id:transaction_id:transaction_type:transaction_time:response_code:timestamp
function Get-NombaSignature([hashtable]$Fields, [string]$Timestamp, [string]$Secret) {
    $rc = if ($Fields.responseCode -eq "null" -or $null -eq $Fields.responseCode) { "" } else { $Fields.responseCode }
    $signingString = "$($Fields.eventType):$($Fields.requestId):$($Fields.userId):$($Fields.walletId):$($Fields.transactionId):$($Fields.transactionType):$($Fields.transactionTime):${rc}:$Timestamp"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    try { [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($signingString))) }
    finally { $hmac.Dispose() }
}

function Read-HttpResponse($httpResp) {
    $reader = [System.IO.StreamReader]::new($httpResp.GetResponseStream())
    $body   = $reader.ReadToEnd()
    $reader.Dispose()
    [PSCustomObject]@{ StatusCode = [int]$httpResp.StatusCode; Content = $body }
}

function Invoke-Webhook([string]$Payload, [string]$Signature, [string]$Timestamp) {
    try {
        $resp = Invoke-WebRequest -Uri "$BaseUrl/webhooks/nomba" -Method Post -Body $Payload `
            -ContentType "application/json" `
            -Headers @{ "nomba-signature" = $Signature; "nomba-timestamp" = $Timestamp; "nomba-signature-algorithm" = "HmacSHA256"; "nomba-signature-version" = "1.0.0" }
        [PSCustomObject]@{ StatusCode = [int]$resp.StatusCode; Content = $resp.Content }
    }
    catch [System.Net.WebException] {
        Read-HttpResponse $_.Exception.Response
    }
}

function Show-Result([string]$Title, $Response, [string]$Expectation) {
    Write-Host "`n=== $Title ===" -ForegroundColor Cyan
    Write-Host ("  HTTP {0}" -f [int]$Response.StatusCode)
    if ($Response.Content) { Write-Host ("  Body: {0}" -f $Response.Content) }
    Write-Host ("  Expected: {0}" -f $Expectation) -ForegroundColor DarkGray
}

# A unique requestId each run so prior runs (24h idempotency TTL) don't block us.
$requestId   = [Guid]::NewGuid().ToString()
$txId        = [Guid]::NewGuid().ToString()
$accountRef  = $AccountRef
$txTime      = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$timestamp   = $txTime   # nomba-timestamp header value

$userId   = "demo-user-001"
$walletId = "demo-wallet-001"

# Real Nomba payment_success shape (vact_transfer)
$payloadObj = @{
    event_type = "payment_success"
    requestId  = $requestId
    data       = @{
        merchant    = @{ userId = $userId; walletId = $walletId; walletBalance = 0 }
        transaction = @{
            transactionId         = $txId
            aliasAccountReference = $accountRef
            type                  = "vact_transfer"
            transactionAmount     = 50000
            responseCode          = ""
            time                  = $txTime
            narration             = "Test payment"
            aliasAccountType      = "VIRTUAL"
            aliasAccountName      = "Test Account"
            aliasAccountNumber    = "0123456789"
        }
        customer = @{ senderName = "Test Sender"; bankName = "Test Bank"; accountNumber = "0000000000"; bankCode = "999" }
    }
}
$payload = $payloadObj | ConvertTo-Json -Depth 10 -Compress

$fields = @{
    eventType       = "payment_success"
    requestId       = $requestId
    userId          = $userId
    walletId        = $walletId
    transactionId   = $txId
    transactionType = "vact_transfer"
    transactionTime = $txTime
    responseCode    = ""
}

$validSig = Get-NombaSignature -Fields $fields -Timestamp $timestamp -Secret $WebhookSecret

# --- Scenario 1: tampered signature -----------------------------------------
$r1 = Invoke-Webhook -Payload $payload -Signature "this-is-not-a-valid-signature" -Timestamp $timestamp
Show-Result "1. Invalid signature (timing-safe HMAC check)" $r1 "401 Unauthorized"

# --- Scenario 2: valid, first delivery --------------------------------------
$r2 = Invoke-Webhook -Payload $payload -Signature $validSig -Timestamp $timestamp
Show-Result "2. Valid webhook (settles ledger)" $r2 '200 + {"status":"accepted"}'

# --- Scenario 3: exact re-delivery (idempotency) ----------------------------
$r3 = Invoke-Webhook -Payload $payload -Signature $validSig -Timestamp $timestamp
Show-Result "3. Duplicate webhook (Redis SETNX)" $r3 '200 + {"status":"duplicate_ignored"}'

# --- Scenario 4: outbound X-Idempotency-Key retry ---------------------------
Write-Host "`n=== 4. Outbound idempotency (X-Idempotency-Key) ===" -ForegroundColor Cyan
$idemKey = [Guid]::NewGuid().ToString()
$orderRef = "ORDER-" + $idemKey.Substring(0, 8)
$checkoutBody = @{ orderReference = $orderRef; amount = 250000; customerEmail = "demo@example.com" } | ConvertTo-Json
$headers = @{ "X-Idempotency-Key" = $idemKey }
if (-not [string]::IsNullOrWhiteSpace($ApiKey)) { $headers["X-Api-Key"] = $ApiKey }

try {
    $c1 = try {
        $r = Invoke-WebRequest -Uri "$BaseUrl/payments/checkout" -Method Post -Body $checkoutBody `
            -ContentType "application/json" -Headers $headers
        [PSCustomObject]@{ StatusCode = [int]$r.StatusCode; Content = $r.Content }
    } catch [System.Net.WebException] { Read-HttpResponse $_.Exception.Response }
    Write-Host ("  First call : HTTP {0} {1}" -f $c1.StatusCode, $c1.Content)

    $c2 = try {
        $r = Invoke-WebRequest -Uri "$BaseUrl/payments/checkout" -Method Post -Body $checkoutBody `
            -ContentType "application/json" -Headers $headers
        [PSCustomObject]@{ StatusCode = [int]$r.StatusCode; Content = $r.Content }
    } catch [System.Net.WebException] { Read-HttpResponse $_.Exception.Response }
    Write-Host ("  Retry call : HTTP {0} {1}" -f $c2.StatusCode, $c2.Content)
    Write-Host '  Expected   : retry returns {"status":"duplicate"} - Nomba NOT called twice' -ForegroundColor DarkGray
}
catch {
    Write-Host "  (skipped/failed - needs valid Nomba sandbox credentials in user-secrets)" -ForegroundColor Yellow
    Write-Host ("  {0}" -f $_.Exception.Message) -ForegroundColor DarkGray
}

Write-Host "`nDemo complete." -ForegroundColor Green
Write-Host "Verify the settled ledger row:" -ForegroundColor Gray
$apiKeyNote = if ($ApiKey) { ' -H "X-Api-Key: <key>"' } else { '' }
Write-Host ("  curl `"$BaseUrl/account/$accountRef/balance`"" + $apiKeyNote) -ForegroundColor Gray
