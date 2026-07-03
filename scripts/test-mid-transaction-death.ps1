<#
.SYNOPSIS
    "Mid-Transaction Death" resilience test.

    Simulates: a payment webhook is accepted and its ledger-settlement job is
    durably enqueued, then the API process dies before any worker touches it.
    Proves that a plain restart (no manual intervention) automatically
    completes the settlement, because Hangfire's job queue is persisted in
    Postgres, not in the crashed process's memory.

    To make the "kill before the job runs" moment DETERMINISTIC (Hangfire's
    Postgres queue is fetched near-instantly via LISTEN/NOTIFY, so racing a
    kill against it would be flaky), this script starts the first instance
    with Hangfire:DisableServer=true — it accepts and enqueues the webhook
    job but has no worker draining the queue, so the job is guaranteed to
    still be sitting Enqueued when we kill it. The restart uses the normal
    (server-enabled) configuration, so recovery is genuinely automatic.

.PARAMETER BaseUrl
    The running API base URL. Default: http://localhost:5292
#>
param(
    [string]$BaseUrl = "http://localhost:5292",
    [string]$WebhookSecret = $env:NOMBA_WEBHOOK_SECRET,
    [string]$PgContainer = "nomba-db",
    [string]$PgUser = "admin",
    [string]$PgDatabase = "nomba_ledger",
    [string]$ProjectDir = "c:\Users\SegunObasooto\source\repos\API\Nomba_Hackathon"
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

function Get-ListenerPid([int]$Port) {
    (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty OwningProcess)
}

function Stop-Api([int]$Port) {
    $p = Get-ListenerPid -Port $Port
    if ($p) {
        Write-Host "  Killing API process (pid $p) ..." -ForegroundColor Yellow
        Stop-Process -Id $p -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
}

function Wait-ApiUp([int]$Port, [int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { return $true }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}

function Invoke-Psql([string]$Sql) {
    (docker exec $PgContainer psql -U $PgUser -d $PgDatabase -t -A -c $Sql).Trim()
}

# --- Step 0: make sure whatever is on :5292 is stopped, then start in --------
# "crash-prone" mode: server disabled, so enqueued jobs are never drained. ----
Write-Host "=== Mid-Transaction Death ===" -ForegroundColor Cyan
Stop-Api -Port 5292

Write-Host "  Starting API with Hangfire server DISABLED (jobs enqueue but never run)..." -ForegroundColor Yellow
Push-Location $ProjectDir
$env:Hangfire__DisableServer = "true"
Start-Process -FilePath "dotnet" -ArgumentList "run","--urls","http://localhost:5292" `
    -RedirectStandardOutput "$env:TEMP\api-crash-mode.log" -RedirectStandardError "$env:TEMP\api-crash-mode.err.log" `
    -WindowStyle Hidden
Remove-Item Env:\Hangfire__DisableServer
Pop-Location

if (-not (Wait-ApiUp -Port 5292)) { Write-Host "API did not come up" -ForegroundColor Red; exit 1 }
Write-Host "  API up (worker-less mode)." -ForegroundColor Green

# --- Step 1: send a real, valid webhook -> accepted -> job enqueued ----------
$requestId  = [Guid]::NewGuid().ToString()
$txId       = [Guid]::NewGuid().ToString()
$accountRef = "midtx-" + $requestId.Substring(0, 8)
$txTime     = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$amount     = 42000

$payloadObj = @{
    event_type = "payment_success"
    requestId  = $requestId
    data       = @{
        merchant    = @{ userId = "midtx-user"; walletId = "midtx-wallet"; walletBalance = 0 }
        transaction = @{
            transactionId = $txId; aliasAccountReference = $accountRef; type = "vact_transfer"
            transactionAmount = $amount; responseCode = ""; time = $txTime
            narration = "Mid-transaction death test"; aliasAccountType = "VIRTUAL"
            aliasAccountName = "MidTx Test"; aliasAccountNumber = "0123456789"
        }
        customer = @{ senderName = "MidTx Sender"; bankName = "Test Bank"; accountNumber = "0000000000"; bankCode = "999" }
    }
}
$payload = $payloadObj | ConvertTo-Json -Depth 10 -Compress
$fields = @{ eventType = "payment_success"; requestId = $requestId; userId = "midtx-user"; walletId = "midtx-wallet"
    transactionId = $txId; transactionType = "vact_transfer"; transactionTime = $txTime; responseCode = "" }
$signature = Get-NombaSignature -Fields $fields -Timestamp $txTime -Secret $WebhookSecret

Write-Host "  Sending webhook for $accountRef ..." -ForegroundColor Yellow
$client = [System.Net.Http.HttpClient]::new()
$req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$BaseUrl/webhooks/nomba")
$req.Content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, "application/json")
$req.Headers.Add("nomba-signature", $signature)
$req.Headers.Add("nomba-timestamp", $txTime)
$resp = $client.SendAsync($req).Result
$body = $resp.Content.ReadAsStringAsync().Result
$client.Dispose()
Write-Host "  Webhook response: HTTP $([int]$resp.StatusCode) $body"

Start-Sleep -Milliseconds 500

# --- Step 2: confirm the job is stuck Enqueued and NO ledger write happened --
$jobState = Invoke-Psql "SELECT statename FROM hangfire.job WHERE arguments::text LIKE '%$requestId%' ORDER BY id DESC LIMIT 1;"
$txBefore = Invoke-Psql "SELECT count(*) FROM transactions WHERE reference_code = '$accountRef';"
Write-Host ""
Write-Host "  Pre-crash Hangfire job state : $jobState  (expect Enqueued)"
Write-Host "  Transactions row for '$accountRef' before crash : $txBefore  (expect 0 - not settled yet)"

# --- Step 3: kill the process ("the crash") ----------------------------------
Stop-Api -Port 5292
$stillUp = Get-ListenerPid -Port 5292
Write-Host "  API killed. Listener on 5292 present: $([bool]$stillUp)"

# --- Step 4: restart normally (server enabled) and let it recover -----------
Write-Host ""
Write-Host "  Restarting API normally (Hangfire server ENABLED) ..." -ForegroundColor Yellow
Push-Location $ProjectDir
Start-Process -FilePath "dotnet" -ArgumentList "run","--urls","http://localhost:5292" `
    -RedirectStandardOutput "$env:TEMP\api-recovered.log" -RedirectStandardError "$env:TEMP\api-recovered.err.log" `
    -WindowStyle Hidden
Pop-Location

if (-not (Wait-ApiUp -Port 5292)) { Write-Host "API did not come back up" -ForegroundColor Red; exit 1 }
Write-Host "  API back up. Waiting for the stranded job to be picked up and settled..." -ForegroundColor Yellow

$deadline = (Get-Date).AddSeconds(30)
$settled = $false
while ((Get-Date) -lt $deadline) {
    $jobState = Invoke-Psql "SELECT statename FROM hangfire.job WHERE arguments::text LIKE '%$requestId%' ORDER BY id DESC LIMIT 1;"
    $txAfter = Invoke-Psql "SELECT count(*) FROM transactions WHERE reference_code = '$accountRef';"
    if ($jobState -eq "Succeeded" -and $txAfter -eq "1") { $settled = $true; break }
    Start-Sleep -Seconds 1
}

$ledgerRow = Invoke-Psql "SELECT count(*), COALESCE(SUM(credit_amount),0), COALESCE(SUM(debit_amount),0) FROM ledgerentries le JOIN transactions t ON t.id = le.transaction_id WHERE t.reference_code = '$accountRef';"
$parts = $ledgerRow -split '\|'
$entryCount = [int]$parts[0]; $totalCredit = [decimal]$parts[1]; $totalDebit = [decimal]$parts[2]
$txAfter = Invoke-Psql "SELECT count(*) FROM transactions WHERE reference_code = '$accountRef';"

Write-Host ""
Write-Host "=== Post-restart verification ===" -ForegroundColor Cyan
Write-Host "  Final Hangfire job state             : $jobState  (expect Succeeded)"
Write-Host "  Transactions rows for '$accountRef'  : $txAfter  (expect 1)"
Write-Host "  LedgerEntries rows                   : $entryCount  (expect 2)"
Write-Host "  Total credit / debit                 : $totalCredit / $totalDebit  (expect equal, == $amount)"

$pass = $settled -and ($jobState -eq "Succeeded") -and ($txBefore -eq "0") -and ($txAfter -eq "1") `
        -and ($entryCount -eq 2) -and ($totalCredit -eq $totalDebit) -and ($totalCredit -eq $amount)

Write-Host ""
if ($pass) {
    Write-Host "RESULT: PASS - job survived the crash and self-healed on restart with no manual step." -ForegroundColor Green
} else {
    Write-Host "RESULT: FAIL - see state above." -ForegroundColor Red
}
exit ([int](-not $pass))
