param(
  # CDP port (app Settings -> CdpPort, default 9222)
  [int]$Port = 9222,
  # target page URL regex (default: the editor main window)
  [string]$UrlPattern = ':5174/?$',
  # fixture name (output <name>.json); default derived from song title/artist
  [string]$Name = '',
  # full output path override (e.g. a location outside the repo / app bundle)
  [string]$OutFile = ''
)
# Capture the currently loaded song (project + analyzed audio) from the running
# editor via CDP into a perf-sim fixture: frontend/public/perf-fixtures/<name>.json.
# Afterwards run the offline replay with: tools\perf\run.ps1 -Fixture '<name>'
# (no audio file / cloud source needed - the sim fakes the AudioBuffer).

$ErrorActionPreference = 'Stop'

function WsSendRecv($ws, $obj) {
  $json = $obj | ConvertTo-Json -Compress -Depth 8
  $bytes = [Text.Encoding]::UTF8.GetBytes($json)
  [void]$ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
  $buf = New-Object byte[] (33554432)
  $sb = New-Object Text.StringBuilder
  do {
    $seg = [ArraySegment[byte]]::new($buf)
    $r = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
  } while (-not $r.EndOfMessage)
  return $sb.ToString()
}

$list = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/list" -Method GET
$target = $null
foreach ($t in $list) {
  if ($t.type -ne 'page') { continue }
  if ($t.url -match $UrlPattern) { $target = $t; break }
}
if (-not $target) { Write-Output "TARGET NOT FOUND (pattern: $UrlPattern)"; exit 1 }

# J.ui is the editor state S (global contract set in boot.ts).
# energy/peaks are Float32Array -> convert to plain arrays, return as a JSON string.
$expr = @'
(async () => {
  const S = (window.J && J.ui) || null;
  if (!S || !S.project) return JSON.stringify({ ok: false, error: 'editor state unavailable (J.ui missing or no project)' });
  const p = S.project, a = S.audio;
  if (!a) return JSON.stringify({ ok: false, error: 'no analyzed audio loaded (load a song first)' });
  return JSON.stringify({
    ok: true,
    capturedAt: new Date().toISOString(),
    sourceUrl: location.href,
    title: p.title || '', artist: p.artist || '',
    audioName: a.name || '',
    project: p,
    audio: {
      name: a.name || '', duration: a.duration, sampleRate: a.sampleRate || 0,
      bpm: a.bpm || 0, beats: a.beats || [], energyRate: a.energyRate || 0,
      energy: Array.from(a.energy || []), peaks: Array.from(a.peaks || []),
    },
    plan: S.plan ? { cuts: S.plan.cuts.length, lines: S.plan.lines.length, duration: S.plan.duration, W: S.plan.W, H: S.plan.H } : null,
  });
})()
'@

$ws = New-Object System.Net.WebSockets.ClientWebSocket
[void]$ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$resp = WsSendRecv $ws @{id = 2; method = 'Runtime.evaluate'; params = @{expression = $expr; returnByValue = $true; awaitPromise = $true} }
$ws.Dispose()

$o = $resp | ConvertFrom-Json
if ($o.error) { Write-Output "CDP ERROR: $($o.error.message)"; exit 1 }
$payload = $o.result.result.value
if (-not $payload) { Write-Output 'EMPTY RESULT'; exit 1 }

$fixture = $payload | ConvertFrom-Json
if (-not $fixture.ok) { Write-Output "CAPTURE FAILED: $($fixture.error)"; exit 1 }

if (-not $Name) {
  $raw = "$($fixture.title) - $($fixture.artist)"
  if ($raw.Trim() -eq ' - ') { $raw = ($fixture.audioName -replace '\.\w+$', '') }
  $Name = (($raw -replace '[\\/:*?"<>|]', '-') -replace '\s+', ' ').Trim()
  if (-not $Name -or $Name -eq '-') { $Name = 'song' }
}

if (-not $OutFile) {
  $outDir = Join-Path $PSScriptRoot '..\..\frontend\public\perf-fixtures'
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
  $OutFile = Join-Path $outDir "$Name.json"
}
[System.IO.File]::WriteAllText($OutFile, $payload, (New-Object System.Text.UTF8Encoding($false)))

$kb = [math]::Round((Get-Item $OutFile).Length / 1KB)
Write-Output "OK -> $OutFile ($($kb)KB)"
Write-Output "song: $($fixture.title) / $($fixture.artist)  audio: $($fixture.audioName)  duration: $([math]::Round($fixture.audio.duration,1))s  bpm: $($fixture.audio.bpm)"
if ($fixture.plan) { Write-Output "plan: cuts=$($fixture.plan.cuts) lines=$($fixture.plan.lines) duration=$([math]::Round($fixture.plan.duration,1))s $($fixture.plan.W)x$($fixture.plan.H)" }
Write-Output "next: tools\perf\run.ps1 -Fixture '$Name'"
