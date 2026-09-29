# 把 CefSharp.BrowserSubprocess.exe 的图标资源替换为应用图标（assets\app.ico）。
# 原理：exe 图标存在 PE 资源里（RT_ICON=3 一组图像 + RT_GROUP_ICON=14 组头），
# 用 Win32 UpdateResource API（rcedit 同款原理）就地替换，不需要第三方工具。
# 由 JizuraLive.csproj 的 PatchCefSubprocessIcon Target 在 Build/Publish 后调用。
param(
    [Parameter(Mandatory = $true)] [string] $TargetExe,
    [Parameter(Mandatory = $true)] [string] $IconPath
)

$ErrorActionPreference = 'Stop'

# 目标 exe 或图标不存在时静默跳过（CefSharp 将来改版移除文件也不炸构建）
if (-not (Test-Path -LiteralPath $TargetExe)) {
    Write-Host "patch-subprocess-icon: skip, not found: $TargetExe"
    exit 0
}
if (-not (Test-Path -LiteralPath $IconPath)) {
    Write-Warning "patch-subprocess-icon: skip, icon not found: $IconPath"
    exit 0
}

Add-Type -Namespace Native -Name Res -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr BeginUpdateResource(string fileName, bool deleteExistingResources);

[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);

[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);
'@

# ---- 解析 .ico：ICONDIR(6字节) + count × ICONDIRENTRY(16字节) ----
$ico = [System.IO.File]::ReadAllBytes($IconPath)
$icoType = [BitConverter]::ToUInt16($ico, 2)
if ($icoType -ne 1) { throw "not a .ico file: $IconPath (type=$icoType)" }
$count = [BitConverter]::ToUInt16($ico, 4)
if ($count -lt 1 -or $count -gt 32) { throw "unexpected icon image count: $count" }

$images = New-Object 'System.Collections.Generic.List[byte[]]'
$entries = New-Object 'System.Collections.Generic.List[byte[]]'   # GRPICONDIRENTRY（末字段是 WORD 资源ID）
for ($i = 0; $i -lt $count; $i++) {
    $off = 6 + 16 * $i
    $size = [BitConverter]::ToUInt32($ico, $off + 8)
    $dataOffset = [BitConverter]::ToUInt32($ico, $off + 12)
    if ($size -le 0 -or ($dataOffset + $size) -gt $ico.Length) { throw "corrupt ICO entry #$i" }

    $img = New-Object byte[] $size
    [Array]::Copy($ico, $dataOffset, $img, 0, $size)
    $images.Add($img)

    $entry = New-Object byte[] 14
    [Array]::Copy($ico, $off, $entry, 0, 12)                        # 宽高/色数/位深/尺寸，原样拷贝
    $entry[12] = [byte](($i + 1) -band 0xFF)                        # WORD nId = RT_ICON 资源名（1 起）
    $entry[13] = [byte]((($i + 1) -shr 8) -band 0xFF)
    $entries.Add($entry)
}

# ---- 组头：WORD reserved(0) + WORD type(1) + WORD count，后接全部 GRPICONDIRENTRY ----
$group = New-Object byte[] (6 + 14 * $count)
[BitConverter]::GetBytes([uint16]0).CopyTo($group, 0)
[BitConverter]::GetBytes([uint16]1).CopyTo($group, 2)
[BitConverter]::GetBytes([uint16]$count).CopyTo($group, 4)
for ($i = 0; $i -lt $count; $i++) { $entries[$i].CopyTo($group, 6 + 14 * $i) }

# ---- 写入 PE 资源（语言 1033 与 CefSharp 原资源一致，直接覆盖不残留）----
$hUpdate = [Native.Res]::BeginUpdateResource($TargetExe, $false)
if ($hUpdate -eq [IntPtr]::Zero) {
    throw "BeginUpdateResource failed for '$TargetExe' (Win32Error=$( [Runtime.InteropServices.Marshal]::GetLastWin32Error() ))，exe 可能正在运行"
}

try {
    $ok = $true
    for ($i = 0; $i -lt $count; $i++) {
        $ok = $ok -and [Native.Res]::UpdateResource($hUpdate, [IntPtr]3, [IntPtr]($i + 1), 1033, $images[$i], [uint32]$images[$i].Length)
    }
    $ok = $ok -and [Native.Res]::UpdateResource($hUpdate, [IntPtr]14, [IntPtr]1, 1033, $group, [uint32]$group.Length)
    if (-not $ok -or -not [Native.Res]::EndUpdateResource($hUpdate, $false)) {
        throw "UpdateResource failed for '$TargetExe' (Win32Error=$( [Runtime.InteropServices.Marshal]::GetLastWin32Error() ))"
    }
} catch {
    # 写入中断时丢弃事务，避免留下半更新的资源
    [void][Native.Res]::EndUpdateResource($hUpdate, $true)
    throw
}

Write-Host "patched icon: $TargetExe ($count image(s))"
exit 0
