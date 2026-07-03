<#
.SYNOPSIS
    "Idempotency Hammer" resilience test: fires the SAME signed Nomba webhook
    payload N times (default 50) concurrently, in well under 500ms, and proves:
      - Exactly 1 request is accepted, the rest come back duplicate_ignored.
      - The database ends up with exactly 1 Transactions row and exactly one
        balanced pair of LedgerEntries (1 credit + 1 debit, equal amounts).

.PARAMETER BaseUrl
    The running API base URL. Default: http://localhost:5292

.PARAMETER WebhookSecret
    The Nomba webhook signing secret. Defaults to $env:NOMBA_WEBHOOK_SECRET.

.PARAMETER Count
    Number of concurrent duplicate deliveries to fire. Default: 50.

.NOTES
    Written for Windows PowerShell 5.1 (no pwsh 7 required). Uses raw
    System.Net.Http.HttpClient + Task.WhenAll so all N requests are dispatched
    back-to-back without waiting on each other's response.
#>
param(
    [string]$BaseUrl = "http://localhost:5292",
    [string]$WebhookSecret = $env:NOMBA_WEBHOOK_SECRET,
    [int]$Count = 50,
    [string]$PgContainer = "nomba-db",
    [string]$PgUser = "admin",
    [string]$PgDatabase = "nomba_ledger"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($WebhookSecret)) {
    Write-Host 'ERROR: webhook secret not supplied. Set $env:NOMBA_WEBHOOK_SECRET or pass -WebhookSecret.' -ForegroundColor Red
    exit 1
}

Add-Type -AssemblyName System.Net.Http

function Get-NombaSignature([hashtable]$Fields, [string]$Timestamp, [string]$Secret) {
    $rc = if ($Fields.responseCode -eq "null" -or $null -eq $Fields.responseCode) { "" } else { $Fields.responseCode }
    $signingString = "$($Fields.eventType):$($Fields.requestId):$($Fields.userId):$($Fields.walletId):$($Fields.transactionId):$($Fields.transactionType):$($Fields.transactionTime):${rc}:$Timestamp"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    try { [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($signingString))) }
    finally { $hmac.Dispose() }
}

# Unique identifiers each run so the 24h Redis idempotency TTL from a prior run
# never masks this run's result.
$requestId  = [Guid]::NewGuid().ToString()
$txId       = [Guid]::NewGuid().ToString()
$accountRef = "hammer-" + $requestId.Substring(0, 8)
$txTime     = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$timestamp  = $txTime
$amount     = 75000
$userId     = "hammer-user"
$walletId   = "hammer-wallet"

$payloadObj = @{
    event_type = "payment_success"
    requestId  = $requestId
    data       = @{
        merchant    = @{ userId = $userId; walletId = $walletId; walletBalance = 0 }
        transaction = @{
            transactionId         = $txId
            aliasAccountReference = $accountRef
            type                  = "vact_transfer"
            transactionAmount     = $amount
            responseCode          = ""
            time                  = $txTime
            narration             = "Idempotency hammer test"
            aliasAccountType      = "VIRTUAL"
            aliasAccountName      = "Hammer Test Account"
            aliasAccountNumber    = "0123456789"
        }
        customer = @{ senderName = "Hammer Sender"; bankName = "Test Bank"; accountNumber = "0000000000"; bankCode = "999" }
    }
}
$payload = $payloadObj | ConvertTo-Json -Depth 10 -Compress

$fields = @{
    eventType = "payment_success"; requestId = $requestId; userId = $userId; walletId = $walletId
    transactionId = $txId; transactionType = "vact_transfer"; transactionTime = $txTime; responseCode = ""
}
$signature = Get-NombaSignature -Fields $fields -Timestamp $timestamp -Secret $WebhookSecret

Write-Host "=== Idempotency Hammer ===" -ForegroundColor Cyan
Write-Host "  Target        : $BaseUrl/webhooks/nomba"
Write-Host "  requestId     : $requestId"
Write-Host "  accountRef    : $accountRef"
Write-Host "  Firing $Count identical requests concurrently..." -ForegroundColor Yellow

$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(30)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$tasks = New-Object 'System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]'
for ($i = 0; $i -lt $Count; $i++) {
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$BaseUrl/webhooks/nomba")
    $req.Content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, "application/json")
    $req.Headers.Add("nomba-signature", $signature)
    $req.Headers.Add("nomba-timestamp", $timestamp)
    $req.Headers.Add("nomba-signature-algorithm", "HmacSHA256")
    $req.Headers.Add("nomba-signature-version", "1.0.0")
    $tasks.Add($client.SendAsync($req))
}
$dispatchMs = $sw.ElapsedMilliseconds
[System.Threading.Tasks.Task]::WaitAll($tasks.ToArray())
$totalMs = $sw.ElapsedMilliseconds

$results = @()
foreach ($t in $tasks) {
    $resp = $t.Result
    $body = $resp.Content.ReadAsStringAsync().Result
    $results += [PSCustomObject]@{ StatusCode = [int]$resp.StatusCode; Body = $body }
}
$client.Dispose()

# @(...) forces an array even when Where-Object matches exactly one item
# (PowerShell unwraps single-item results, which would make .Count $null).
$accepted  = (@($results | Where-Object { $_.Body -match '"accepted"' })).Count
$duplicate = (@($results | Where-Object { $_.Body -match '"duplicate_ignored"' })).Count
$other     = $results.Count - $accepted - $duplicate
$non200    = (@($results | Where-Object { $_.StatusCode -ne 200 })).Count

Write-Host ""
Write-Host "  Dispatch time (queuing all $Count requests) : ${dispatchMs}ms"
Write-Host "  Total round-trip time                        : ${totalMs}ms"
Write-Host "  HTTP 200 responses                           : $($Count - $non200)/$Count"
Write-Host "  { status: accepted }                         : $accepted"
Write-Host "  { status: duplicate_ignored }                : $duplicate"
if ($other -gt 0) {
    Write-Host "  Other/unexpected responses                   : $other" -ForegroundColor Red
    $results | Where-Object { $_.Body -notmatch '"accepted"' -and $_.Body -notmatch '"duplicate_ignored"' } | ForEach-Object {
        Write-Host "    HTTP $($_.StatusCode): $($_.Body)" -ForegroundColor Red
    }
}

# The webhook only enqueues a Hangfire job for the accepted event; give the
# background worker a moment to actually write the ledger entries.
Write-Host ""
Write-Host "  Waiting for background settlement job to complete..." -ForegroundColor Yellow
Start-Sleep -Seconds 3

$txCountSql = "SELECT count(*) FROM transactions WHERE reference_code = '$accountRef';"
$txCount = (docker exec $PgContainer psql -U $PgUser -d $PgDatabase -t -A -c $txCountSql).Trim()

$ledgerSql = "SELECT count(*), COALESCE(SUM(credit_amount),0), COALESCE(SUM(debit_amount),0) FROM ledgerentries le JOIN transactions t ON t.id = le.transaction_id WHERE t.reference_code = '$accountRef';"
$ledgerRow = (docker exec $PgContainer psql -U $PgUser -d $PgDatabase -t -A -F',' -c $ledgerSql).Trim()
$ledgerParts = $ledgerRow -split ','
$entryCount = [int]$ledgerParts[0]
$totalCredit = [decimal]$ledgerParts[1]
$totalDebit  = [decimal]$ledgerParts[2]

Write-Host ""
Write-Host "=== Database verification ===" -ForegroundColor Cyan
Write-Host "  Transactions rows for '$accountRef'  : $txCount  (expect 1)"
Write-Host "  LedgerEntries rows                   : $entryCount  (expect 2 - one balanced pair)"
Write-Host "  Total credit / debit                 : $totalCredit / $totalDebit  (expect equal, == $amount)"

$pass = ($accepted -eq 1) -and ($duplicate -eq ($Count - 1)) -and ($non200 -eq 0) -and
        ($txCount -eq "1") -and ($entryCount -eq 2) -and ($totalCredit -eq $totalDebit) -and ($totalCredit -eq $amount)

Write-Host ""
if ($pass) {
    Write-Host "RESULT: PASS - exactly one settlement, $($Count-1) duplicates ignored, ledger balanced." -ForegroundColor Green
} else {
    Write-Host "RESULT: FAIL - see counts above." -ForegroundColor Red
}
exit ([int](-not $pass))
