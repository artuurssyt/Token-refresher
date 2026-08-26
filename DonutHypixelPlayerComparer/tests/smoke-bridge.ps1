# Simulates the PlayerCheckerBridge Minecraft module against a running desktop bridge.
# Usage: powershell -File tests\smoke-bridge.ps1 -Username ARTUURSS [-Port 47891]
param(
    [string]$Username = "ARTUURSS",
    [int]$Port = 47891
)

$base = "http://127.0.0.1:$Port"
Write-Host "Polling $base as a fake PlayerCheckerBridge client for '$Username'..."

try {
    $status = Invoke-RestMethod "$base/api/v1/status" -TimeoutSec 5
} catch {
    Write-Host "No bridge listening on $base. Start a scan or press 'Test bridge' in the app first." -ForegroundColor Red
    exit 1
}
Write-Host ("Bridge is up. queueDepth={0} commandTemplate='{1}'" -f $status.queueDepth, $status.commandTemplate)

Invoke-RestMethod "$base/api/v1/heartbeat" -Method Post -ContentType "application/json" -Body (@{
    clientVersion = "smoke-test"; serverAddress = "donutsmp.net"; playerName = "SmokeTester"
} | ConvertTo-Json) | Out-Null

$job = $null
for ($i = 0; $i -lt 100; $i++) {
    $response = Invoke-WebRequest "$base/api/v1/job" -TimeoutSec 5 -SkipHttpErrorCheck
    if ($response.StatusCode -eq 200) { $job = $response.Content | ConvertFrom-Json; break }
    Start-Sleep -Milliseconds 200
}
if ($null -eq $job) { Write-Host "Bridge never offered a job. Is a scan running?" -ForegroundColor Red; exit 1 }
Write-Host ("Took job {0} for {1}" -f $job.jobId, $job.username)

# The same payload shape the real module posts after reading the stats view.
$payload = @{
    money = 2200000000; shards = 1204; playtimeSeconds = 442800
    kills = 406; deaths = 166; mobsKilled = 500
    brokenBlocks = 14300; placedBlocks = 4200
    moneyMadeFromSell = 0; moneySpentOnShop = 0; rank = ""; location = ""
    rawGuiText = @{
        "10" = @("Money", "$ 2.2B"); "11" = @("Shards", "1,204")
        "12" = @("Kills", "406");    "13" = @("Deaths", "166")
        "14" = @("Playtime", "5d 3h")
    }
} | ConvertTo-Json -Depth 6

$done = Invoke-WebRequest "$base/api/v1/job/$($job.jobId)/complete" -Method Post `
    -ContentType "application/json" -Body $payload -SkipHttpErrorCheck
if ($done.StatusCode -ge 200 -and $done.StatusCode -lt 300) {
    Write-Host "Bridge accepted the stats. The row should now show money 2,200,000,000." -ForegroundColor Green
} else {
    Write-Host ("Bridge rejected the payload: {0} {1}" -f $done.StatusCode, $done.Content) -ForegroundColor Red
    exit 1
}
