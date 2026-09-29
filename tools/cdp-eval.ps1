param(
  [Parameter(Mandatory=$true)][string]$Expr,
  [switch]$Mirror
)

$ErrorActionPreference = 'Stop'

function WsSendRecv($ws, $obj) {
  $json = $obj | ConvertTo-Json -Compress -Depth 8
  $bytes = [Text.Encoding]::UTF8.GetBytes($json)
  $ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
  $buf = New-Object byte[] (4194304)
  $sb = New-Object Text.StringBuilder
  do {
    $seg = [ArraySegment[byte]]::new($buf)
    $r = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
  } while (-not $r.EndOfMessage)
  return $sb.ToString()
}

$list = Invoke-RestMethod -Uri 'http://127.0.0.1:9222/json/list' -Method GET
$target = $null
foreach ($t in $list) {
  if ($t.type -ne 'page') { continue }
  $ws = New-Object System.Net.WebSockets.ClientWebSocket
  $ws.ConnectAsync([Uri]$t.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
  $resp = WsSendRecv $ws @{id=1; method='Runtime.evaluate'; params=@{expression='window.__jl_spout_mirror === true'; returnByValue=$true}}
  $ws.Dispose()
  $o = $resp | ConvertFrom-Json
  $isMirror = [bool]($o.result.result.value)
  if ($isMirror -eq [bool]$Mirror.IsPresent) { $target = $t; break }
}
if (-not $target) { Write-Output 'TARGET NOT FOUND'; exit 1 }

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
$resp = WsSendRecv $ws @{id=2; method='Runtime.evaluate'; params=@{expression=$Expr; returnByValue=$true; awaitPromise=$true}}
Write-Output $resp
$ws.Dispose()
