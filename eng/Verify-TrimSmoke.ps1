<#
.SYNOPSIS
    Publishes the MCPFab trim sample with PublishTrimmed, runs it, and asserts it still works.

.DESCRIPTION
    Every trim failure in this stack is silent rather than loud:

      * WithToolsFromAssembly is [RequiresUnreferencedCode] and uses Assembly.GetTypes(). Trimmed,
        the server starts happily and advertises zero tools.
      * Anonymous types passed to JsonSerializer.Serialize, and returned from minimal-API
        handlers, serialise to {} instead of throwing.

    So this script asserts payload CONTENT, not just that the process started. A check that only
    confirms a 200 from /healthz would pass in both failure modes above.
#>
[CmdletBinding()]
param(
    [string] $Project = "samples/DnaX.MCPFab.TrimSample/DnaX.MCPFab.TrimSample.csproj",
    [string] $RuntimeIdentifier,
    [int] $Port = 5798
)

$ErrorActionPreference = "Stop"

if (-not $RuntimeIdentifier) {
    $RuntimeIdentifier = if ($IsWindows -or $env:OS -eq "Windows_NT") { "win-x64" } else { "linux-x64" }
}

$publishDir = Join-Path ([System.IO.Path]::GetTempPath()) "mcpfab-trimsmoke-$([Guid]::NewGuid().ToString('N'))"

Write-Output "Publishing $Project trimmed for $RuntimeIdentifier ..."
# Trimming requires a self-contained publish; a framework-dependent one silently ignores it.
& dotnet publish $Project -c Release -r $RuntimeIdentifier --self-contained true `
    -p:PublishTrimmed=true -o $publishDir --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Trimmed publish failed with exit code $LASTEXITCODE." }

$exeName = if ($RuntimeIdentifier -like "win-*") { "DnaX.MCPFab.TrimSample.exe" } else { "DnaX.MCPFab.TrimSample" }
$exePath = Join-Path $publishDir $exeName
if (-not (Test-Path $exePath)) { throw "Published executable not found at '$exePath'." }

$sizeMb = [Math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Output "Published trimmed executable: $exeName ($sizeMb MB)"

$process = $null
try {
    $startArgs = @{
        FilePath         = $exePath
        ArgumentList     = @("--Server:Port=$Port", "--Server:Host=localhost")
        WorkingDirectory = $publishDir
        PassThru         = $true
    }
    # -WindowStyle is Windows-only; PowerShell Core on Linux rejects it outright.
    if ($IsWindows -or $env:OS -eq "Windows_NT") { $startArgs.WindowStyle = "Hidden" }
    $process = Start-Process @startArgs

    $base = "http://localhost:$Port"
    $ready = $false
    foreach ($attempt in 1..40) {
        if ($process.HasExited) { throw "Server exited early with code $($process.ExitCode)." }
        try {
            Invoke-RestMethod -Uri "$base/healthz" -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw "Server did not become healthy on $base/healthz." }

    # --- 1. Health payload must carry real properties, not {} ---
    $health = Invoke-RestMethod -Uri "$base/healthz" -TimeoutSec 5
    if (-not $health.status) { throw "Health payload has no 'status'. Trimming stripped the response properties." }
    if ($null -eq $health.items) { throw "Health payload has no 'items'. The server-supplied health callback was stripped." }
    Write-Output "Health payload intact: status=$($health.status) items=$($health.items)"

    $headers = @{ "Content-Type" = "application/json"; "Accept" = "application/json, text/event-stream" }

    function Invoke-Rpc([string] $body) {
        $raw = Invoke-WebRequest -Uri "$base/mcp" -Method Post -Headers $headers -Body $body -TimeoutSec 15
        # Streamable HTTP replies as SSE; the JSON payload is on the data: line.
        $line = ($raw.Content -split "`n" | Where-Object { $_ -like "data: *" } | Select-Object -First 1)
        $json = if ($line) { $line.Substring(6) } else { $raw.Content }
        return $json | ConvertFrom-Json
    }

    $init = Invoke-Rpc '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"trim-smoke","version":"1.0"}}}'
    if ($init.error) { throw "initialize failed: $($init.error | ConvertTo-Json -Compress)" }

    # --- 2. Tools must still be advertised. Zero tools is the WithToolsFromAssembly failure. ---
    $tools = Invoke-Rpc '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    if ($tools.error) { throw "tools/list failed: $($tools.error | ConvertTo-Json -Compress)" }
    $count = @($tools.result.tools).Count
    if ($count -lt 1) { throw "tools/list returned $count tools. Trimming stripped the tool classes." }
    Write-Output "tools/list returned $count tools."

    # --- 3. A tool result must contain real properties, not {} ---
    $call = Invoke-Rpc '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"sample_get_item","arguments":{"key":"alpha"}}}'
    if ($call.error) { throw "tools/call failed: $($call.error | ConvertTo-Json -Compress)" }
    $text = @($call.result.content)[0].text
    if (-not $text) { throw "tools/call returned no text content." }
    $payload = $text | ConvertFrom-Json
    if ($payload.key -ne "alpha") { throw "Tool payload lost its properties (got '$text'). This is the anonymous-type {} failure mode." }
    if ($payload.value -ne "first") { throw "Tool payload 'value' missing (got '$text')." }
    if ($payload.length -ne 5) { throw "Tool payload 'length' missing (got '$text'). McpJson.Scalar was stripped." }
    Write-Output "tools/call payload intact: $text"

    Write-Output "Trim smoke test passed: trimmed server advertises $count tools and returns complete payloads."
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}
