param(
  [int]$Port = 9222,
  [string]$BaseUrl = 'http://localhost:5174',
  # fixture name under frontend/public/perf-fixtures/ (without .json), or a full URL/path
  [string]$Fixture = 'jinge2',
  # preview (1280px canvas) | full (plan.W, export-like load)
  [string]$Res = 'preview',
  # sweep rate in Hz (60 = every frame; 30 halves the wall time)
  [int]$SweepHz = 60,
  # sweep only first N seconds (0 = whole song)
  [int]$SweepSec = 0,
  # realtime playback sim seconds (0 = skip)
  [int]$RtSec = 30,
  # auto | boot | start-end
  [string]$RtWin = 'auto',
  # include tech-thumbnail sim phase
  [switch]$Thumbs,
  [int]$ReplanN = 5,
  # Chrome-trybot-style measurement: warmup runs are discarded, N measured runs
  # are aggregated by median (worst-case metrics use max-of-runs)
  [int]$Reps = 3,
  [int]$Warmup = 1,
  # CPU throttling rate (CDP Emulation.setCPUThrottlingRate): 1 = off, 4 = "4x slower machine"
  [double]$Throttle = 1,
  # extra unmeasured run under the V8 CPU profiler; writes .profile.txt with top self-time functions
  [switch]$Profile,
  # append one summary line per invocation to tools/perf/history.jsonl
  [switch]$NoHistory,
  [int]$TimeoutSec = 1800,
  [string]$OutDir = '',
  # baseline report to diff against (median vs median; >+10% marks REGRESSION)
  [string]$Baseline = '',
  # fail (exit 1) on baseline regression, not just warn
  [switch]$StrictBaseline,
  # reuse an existing /perfsim tab instead of opening a new one
  [switch]$ReuseTab
)
# Simulated performance test runner (Chrome-tools-style).
# 1. opens /perfsim in the app (CDP) with a captured song fixture
# 2. optional CPU throttling; warmup + N measured repetitions
# 3. aggregates by median, compares against thresholds and baseline
# 4. saves the JSON report, appends history, prints PASS/FAIL
# See tools/perf/README.md for the full workflow.

$ErrorActionPreference = 'Stop'

function WsRead($ws) {
  $buf = New-Object byte[] (33554432)
  $sb = New-Object Text.StringBuilder
  do {
    $seg = [ArraySegment[byte]]::new($buf)
    $r = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
  } while (-not $r.EndOfMessage)
  return $sb.ToString() | ConvertFrom-Json
}

function WsEval($ws, $id, $expr, [bool]$awaitPromise = $false) {
  # send one command, read frames until the response with matching id arrives
  # (events may interleave; they are discarded here)
  $msg = @{ id = $id; method = 'Runtime.evaluate'; params = @{ expression = $expr; returnByValue = $true; awaitPromise = $awaitPromise } } | ConvertTo-Json -Compress -Depth 8
  $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
  [void]$ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
  while ($true) {
    $o = WsRead $ws
    if ($o.PSObject.Properties['id'] -and $o.id -eq $id) { return $o }
  }
}

function WsCmd($ws, $id, $method, $params = @{}) {
  $msg = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 8
  $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
  [void]$ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
  while ($true) {
    $o = WsRead $ws
    if ($o.PSObject.Properties['id'] -and $o.id -eq $id) { return $o }
  }
}

function Find-Page($list, $pattern) {
  foreach ($t in $list) { if ($t.type -eq 'page' -and $t.url -match $pattern) { return $t } }
  return $null
}

function Median($nums) {
  $s = @($nums | Sort-Object)
  $n = $s.Count
  if ($n -eq 0) { return 0 }
  if ($n % 2 -eq 1) { return [double]$s[[int](($n - 1) / 2)] }
  return ([double]$s[$n / 2 - 1] + [double]$s[$n / 2]) / 2
}

$script:nextId = 100

function Poll-Sim($ws) {
  $resp = WsEval $ws $script:nextId "(() => { const p = window.__perfsim; if (!p) return JSON.stringify({ status: 'boot' }); return JSON.stringify({ status: p.status, phase: p.phase, progress: p.progress, error: p.error, quick: p.quick, startedAt: p.startedAt }); })()"
  $script:nextId++
  return $resp.result.result.value | ConvertFrom-Json
}

function Current-StartedAt($ws) {
  $st = Poll-Sim $ws
  if ($null -ne $st.startedAt) { return [long][double]$st.startedAt }
  return -1
}

function Wait-Done($ws, $deadline, [string]$label, [long]$expectStartedAt) {
  $lastLine = ''
  while ($true) {
    Start-Sleep -Seconds 2
    $st = Poll-Sim $ws
    if ($st.error) { Write-Host "SIM ERROR ($label): $($st.error)"; exit 3 }
    $pct = if ($null -ne $st.progress) { [int]([double]$st.progress * 100) } else { 0 }
    $line = "[$label] [$($st.status)] $($st.phase) ${pct}%"
    if ($line -ne $lastLine) { Write-Host $line; $lastLine = $line }
    # startedAt 必须换新：防止导航生效前读到上一轮页面的 done
    if ($st.status -eq 'done' -and [long][double]$st.startedAt -ne $expectStartedAt) { return }
    if ((Get-Date) -gt $deadline) { Write-Host "TIMEOUT ($label)"; exit 4 }
  }
}

# ---- build sim URL ----
$qs = "fixture=$([Uri]::EscapeDataString($Fixture))&res=$Res&hz=$SweepHz&sweep=$SweepSec&rt=$RtSec&rtwin=$RtWin&replann=$ReplanN&probe=1"
if ($Thumbs) { $qs += '&thumbs=1' }
$simUrl = "$BaseUrl/perfsim?$qs"

# ---- open (or reuse) the tab ----
if (-not $ReuseTab) {
  $enc = [Uri]::EscapeDataString($simUrl)
  $created = $false
  try { Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/new?$enc" -Method PUT | Out-Null; $created = $true } catch {}
  if (-not $created) {
    try { Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/new?$enc" -Method GET | Out-Null; $created = $true } catch {}
  }
  if (-not $created) { Write-Host 'FAILED TO OPEN TAB (/json/new unsupported)'; exit 2 }
  Start-Sleep -Milliseconds 1500
}

$list = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/list" -Method GET
$target = Find-Page $list '/perfsim'
if (-not $target) { Write-Host 'PERFSIM TAB NOT FOUND'; exit 2 }

$ws = New-Object System.Net.WebSockets.ClientWebSocket
[void]$ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

# environment controls (persist across navigations of this target)
if ($Throttle -gt 1) {
  [void](WsCmd $ws $script:nextId 'Emulation.setCPUThrottlingRate' @{ rate = $Throttle }); $script:nextId++
  Write-Host "CPU throttling: ${Throttle}x"
}

Write-Host "SIM  $simUrl"
$deadline = (Get-Date).AddSeconds($TimeoutSec)

function Invoke-Rep([int]$n, [string]$label) {
  $oldStartedAt = Current-StartedAt $ws
  $nav = "location.href = '$($simUrl -replace "'", "\'")&rep=$n'"
  [void](WsEval $ws $script:nextId $nav); $script:nextId++
  Start-Sleep -Seconds 3
  Wait-Done $ws $deadline $label $oldStartedAt
  $resp = WsEval $ws $script:nextId 'window.__perfsim.report'; $script:nextId++
  if (-not $resp.result.result.value) { Write-Host "NO REPORT ($label)"; exit 3 }
  return $resp.result.result.value
}

# ---- warmup ----
for ($w = 1; $w -le $Warmup; $w++) { [void](Invoke-Rep -n 0 -label "warmup$w") }

# ---- CPU profile run (extra, unmeasured) ----
$prof = $null; $profRows = $null
if ($Profile) {
  [void](WsCmd $ws $script:nextId 'Profiler.enable'); $script:nextId++
  [void](WsCmd $ws $script:nextId 'Profiler.start'); $script:nextId++
  [void](Invoke-Rep -n 0 -label 'profile')
  $prof = (WsCmd $ws $script:nextId 'Profiler.stop').result.profile; $script:nextId++
  $nodeById = @{}
  foreach ($nd in $prof.nodes) { $nodeById[[string]$nd.id] = $nd }
  $hits = @{}
  foreach ($s in $prof.samples) {
    $nid = [string]$s
    if (-not $hits.ContainsKey($nid)) { $hits[$nid] = 0 }
    $hits[$nid]++
  }
  $spanUs = [double]($prof.endTime - $prof.startTime)
  $totalHits = [math]::Max(1, $prof.samples.Count)
  $profRows = foreach ($kv in $hits.GetEnumerator()) {
    $nd = $nodeById[$kv.Key]
    if ($nd) {
      $cf = $nd.callFrame
      [pscustomobject]@{
        selfMs = [math]::Round($kv.Value / $totalHits * $spanUs / 1000, 1)
        hits = $kv.Value
        fn = if ($cf.functionName) { $cf.functionName } else { '(anonymous)' }
        loc = ('{0}:{1}' -f ($cf.url -replace '^.*/', ''), $cf.lineNumber)
      }
    }
  }
}

# ---- measured reps ----
$reports = @()
for ($r = 1; $r -le $Reps; $r++) { $reports += ,(Invoke-Rep -n $r -label "rep$r") }

# ---- aggregate (median; worst-case metrics take max-of-runs) ----
function Med($sel) { Median (@($reports | ForEach-Object $sel)) }
function MaxOf($sel) { (@($reports | ForEach-Object $sel) | Measure-Object -Maximum).Maximum }

$merged = $reports[0]
$merged.replan = [pscustomobject]@{
  n = $merged.replan.n
  avg = [math]::Round((Med { $_.replan.avg }), 2)
  p50 = [math]::Round((Med { $_.replan.p50 }), 2)
  p95 = [math]::Round((Med { $_.replan.p95 }), 2)
  max = [math]::Round((MaxOf { $_.replan.max }), 2)
}
$merged.sweep.draw = [pscustomobject]@{
  n = $merged.sweep.draw.n
  avg = [math]::Round((Med { $_.sweep.draw.avg }), 2)
  p50 = [math]::Round((Med { $_.sweep.draw.p50 }), 2)
  p95 = [math]::Round((Med { $_.sweep.draw.p95 }), 2)
  p99 = [math]::Round((Med { $_.sweep.draw.p99 }), 2)
  max = [math]::Round((MaxOf { $_.sweep.draw.max }), 2)
}
if ($merged.realtime) {
  $merged.realtime.droppedPct = [math]::Round((Med { $_.realtime.droppedPct }), 2)
  $merged.realtime.gapMax = [math]::Round((MaxOf { $_.realtime.gapMax }), 1)
  $merged.realtime.draw = [pscustomobject]@{
    n = $merged.realtime.draw.n
    avg = [math]::Round((Med { $_.realtime.draw.avg }), 2)
    p50 = [math]::Round((Med { $_.realtime.draw.p50 }), 2)
    p95 = [math]::Round((Med { $_.realtime.draw.p95 }), 2)
    p99 = [math]::Round((Med { $_.realtime.draw.p99 }), 2)
    max = [math]::Round((MaxOf { $_.realtime.draw.max }), 2)
  }
  $merged.realtime.longTasks = [pscustomobject]@{
    count = (MaxOf { $_.realtime.longTasks.count })
    worstMs = [math]::Round((MaxOf { $_.realtime.longTasks.worstMs }), 1)
  }
}
if ($merged.thumbs) {
  $merged.thumbs.plan = [pscustomobject]@{ n = $merged.thumbs.plan.n; avg = [math]::Round((Med { $_.thumbs.plan.avg }), 2); max = [math]::Round((MaxOf { $_.thumbs.plan.max }), 2) }
  $merged.thumbs.paint = [pscustomobject]@{ n = $merged.thumbs.paint.n; avg = [math]::Round((Med { $_.thumbs.paint.avg }), 2); max = [math]::Round((MaxOf { $_.thumbs.paint.max }), 2) }
}

# re-evaluate thresholds on aggregated numbers (mirror of perfsim.ts THR)
$thr = $merged.thr
$fails = @()
if ($merged.replan.p50 -gt $thr.replanP50Max) { $fails += "replan p50 $($merged.replan.p50)ms > $($thr.replanP50Max)ms" }
if ($merged.sweep.draw.p95 -gt $thr.sweepDrawP95Max) { $fails += "sweep draw p95 $($merged.sweep.draw.p95)ms > $($thr.sweepDrawP95Max)ms" }
if ($merged.sweep.draw.max -gt $thr.sweepDrawMax) { $fails += "sweep draw max $($merged.sweep.draw.max)ms > $($thr.sweepDrawMax)ms" }
if ($merged.realtime) {
  if ($merged.realtime.droppedPct -gt $thr.rtDroppedPctMax) { $fails += "rt dropped $($merged.realtime.droppedPct)% > $($thr.rtDroppedPctMax)%" }
  if ($merged.realtime.gapMax -gt $thr.rtGapMaxMax) { $fails += "rt gapMax $($merged.realtime.gapMax)ms > $($thr.rtGapMaxMax)ms" }
  if ($merged.realtime.draw.p95 -gt $thr.rtDrawP95Max) { $fails += "rt draw p95 $($merged.realtime.draw.p95)ms > $($thr.rtDrawP95Max)ms" }
  if ($merged.realtime.longTasks.worstMs -gt $thr.rtLongTaskMax) { $fails += "rt longtask $($merged.realtime.longTasks.worstMs)ms > $($thr.rtLongTaskMax)ms" }
}
if ($merged.thumbs -and $merged.thumbs.paint.avg -gt $thr.thumbPaintAvgMax) { $fails += "thumb paint avg $($merged.thumbs.paint.avg)ms > $($thr.thumbPaintAvgMax)ms" }

$repSummary = @()
foreach ($r in $reports) {
  $repSummary += [pscustomobject]@{
    sweepDrawP95 = $r.sweep.draw.p95; sweepDrawMax = $r.sweep.draw.max
    rtDroppedPct = if ($r.realtime) { $r.realtime.droppedPct } else { $null }
    rtGapMax = if ($r.realtime) { $r.realtime.gapMax } else { $null }
  }
}
$merged | Add-Member -NotePropertyName reps -NotePropertyValue $repSummary -Force
$merged | Add-Member -NotePropertyName aggregation -NotePropertyValue @{ reps = $Reps; warmup = $Warmup; throttle = $Throttle; method = 'median (worst-case: max-of-runs)' } -Force
$merged.verdict.failures = $fails
$merged.verdict.pass = ($fails.Count -eq 0)

# ---- save ----
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'reports' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outFile = Join-Path $OutDir "$Fixture-$Res-$stamp.json"
[System.IO.File]::WriteAllText($outFile, ($merged | ConvertTo-Json -Depth 24), (New-Object System.Text.UTF8Encoding($false)))

$profFile = $null
if ($Profile -and $prof) {
  $profFile = Join-Path $OutDir "$Fixture-$Res-$stamp.profile.txt"
  $top = @($profRows | Sort-Object selfMs -Descending | Select-Object -First 25)
  $lines = @("CPU profile (top self-time)", "")
  foreach ($t in $top) { $lines += ('{0,9} ms  {1,7} hits  {2}  {3}' -f $t.selfMs, $t.hits, $t.fn, $t.loc) }
  [System.IO.File]::WriteAllText($profFile, (($lines -join "`r`n") + "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
}

# ---- history ----
if (-not $NoHistory) {
  $git = 'n/a'
  try { $git = (git -C (Join-Path $PSScriptRoot '..\..') rev-parse --short HEAD) 2>$null } catch {}
  $hist = [pscustomobject]@{
    ts = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ss'); git = $git
    fixture = $Fixture; res = $Res; hz = $SweepHz; throttle = $Throttle; reps = $Reps
    replanP50 = $merged.replan.p50
    sweepAvg = $merged.sweep.draw.avg; sweepP95 = $merged.sweep.draw.p95; sweepMax = $merged.sweep.draw.max
    rtDroppedPct = if ($merged.realtime) { $merged.realtime.droppedPct } else { $null }
    rtGapMax = if ($merged.realtime) { $merged.realtime.gapMax } else { $null }
    rtDrawP95 = if ($merged.realtime) { $merged.realtime.draw.p95 } else { $null }
    verdict = if ($merged.verdict.pass) { 'PASS' } else { 'FAIL' }
    report = (Split-Path $outFile -Leaf)
  }
  [System.IO.File]::AppendAllText((Join-Path $PSScriptRoot 'history.jsonl'), ($hist | ConvertTo-Json -Compress) + "`r`n")
}

# ---- summary ----
$d = $merged.sweep.draw
Write-Output ''
Write-Output ("song      : {0}  ({1}s, bpm {2}, {3} cuts / {4} lines)" -f $merged.song.audio, $merged.song.duration, $merged.song.bpm, $merged.plan.cuts, $merged.plan.lines)
Write-Output ("method    : {0} warmup + {1} reps{2}, median (worst-case: max)" -f $Warmup, $Reps, $(if ($Throttle -gt 1) { ", CPU ${Throttle}x" } else { '' }))
Write-Output ("replan    : p50 {0} ms  max {1} ms  (n={2})" -f $merged.replan.p50, $merged.replan.max, $merged.replan.n)
Write-Output ("sweep     : {0} frames @{1}Hz {2}px -> avg {3} / p95 {4} / max {5} ms" -f $merged.sweep.frames, $merged.sweep.hz, $merged.sweep.canvasW, $d.avg, $d.p95, $d.max)
if ($merged.sweep.worstCuts.Count -gt 0) {
  $w = $merged.sweep.worstCuts[0]
  Write-Output ("worst cut : #{0} t={1}s {2}/{3}->{4} max {5} ms (avg {6})" -f $w.cut, $w.t, $w.layout, $w.enter, $w.exit, $w.max, $w.avg)
}
if ($merged.realtime) {
  $r = $merged.realtime
  Write-Output ("realtime  : {0}-{1}s -> dropped {2}%  gapMax {3} ms  draw p95 {4} / max {5} ms  longtask {6} (worst {7} ms)" -f $r.window[0], $r.window[1], $r.droppedPct, $r.gapMax, $r.draw.p95, $r.draw.max, $r.longTasks.count, $r.longTasks.worstMs)
}
if ($merged.thumbs) {
  Write-Output ("thumbs    : {0} cards -> plan avg {1} ms, paint avg {2} / max {3} ms" -f $merged.thumbs.cards, $merged.thumbs.plan.avg, $merged.thumbs.paint.avg, $merged.thumbs.paint.max)
}
if ($merged.memory.afterRtMB) {
  Write-Output ("heap      : {0} -> {1} -> {2} MB" -f $merged.memory.beforeMB, $merged.memory.afterSweepMB, $merged.memory.afterRtMB)
}
$i = 0
foreach ($rs in $repSummary) { $i++; Write-Output ("  rep{0}     : sweep p95 {1} / max {2} ms   rt dropped {3}%  gapMax {4} ms" -f $i, $rs.sweepDrawP95, $rs.sweepDrawMax, $rs.rtDroppedPct, $rs.rtGapMax) }

$regressed = $false
if ($Baseline -and (Test-Path $Baseline)) {
  $base = Get-Content $Baseline -Raw | ConvertFrom-Json
  $checks = @(
    @{ name = 'sweep.draw.p95'; now = $merged.sweep.draw.p95; old = $base.sweep.draw.p95 },
    @{ name = 'sweep.draw.max'; now = $merged.sweep.draw.max; old = $base.sweep.draw.max },
    @{ name = 'replan.p50'; now = $merged.replan.p50; old = $base.replan.p50 }
  )
  if ($merged.realtime -and $base.realtime) {
    $checks += @(
      @{ name = 'rt.droppedPct'; now = $merged.realtime.droppedPct; old = $base.realtime.droppedPct },
      @{ name = 'rt.gapMax'; now = $merged.realtime.gapMax; old = $base.realtime.gapMax }
    )
  }
  foreach ($c in $checks) {
    if ($null -eq $c.old -or $null -eq $c.now) { continue }
    $delta = if ($c.old -gt 0) { [math]::Round((($c.now - $c.old) / $c.old) * 100) } else { 0 }
    $flag = 'flat'
    if ($delta -gt 10 -and ($c.now - $c.old) -gt 0.5) { $flag = 'REGRESSION'; $regressed = $true }
    elseif ($delta -lt -10 -and ($c.old - $c.now) -gt 0.5) { $flag = 'improved' }
    Write-Output ("baseline  : {0}  {1} -> {2} ({3}%) {4}" -f $c.name, $c.old, $c.now, $delta, $flag)
  }
}

Write-Output ''
if ($profFile) {
  Write-Output "profile   : $profFile"
  Get-Content $profFile | Select-Object -First 10 | ForEach-Object { Write-Output ("  " + $_) }
}
if ($merged.verdict.pass) { Write-Output 'VERDICT: PASS' } else {
  Write-Output 'VERDICT: FAIL'
  foreach ($f in $fails) { Write-Output "  - $f" }
}
Write-Output "report   : $outFile"
if (-not $merged.verdict.pass) { exit 1 }
if ($regressed -and $StrictBaseline) { Write-Output 'BASELINE REGRESSION (-StrictBaseline)'; exit 1 }
exit 0
