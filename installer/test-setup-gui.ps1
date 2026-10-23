# セットアップ画面を UI オートメーションで操作するテスト（テスト用の場所だけを使う）
#   pwsh -File installer\test-setup-gui.ps1
$ErrorActionPreference = 'Stop'
if (-not ('GuiW' -as [type])) { Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class GuiW {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  static bool IsButton(IntPtr h) { var c = new StringBuilder(256); GetClassName(h, c, 256); return c.ToString().ToUpperInvariant().Contains("BUTTON"); }
  public static IntPtr TopWindow(int pid) { IntPtr f = IntPtr.Zero; EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { f = h; return false; } return true; }, IntPtr.Zero); return f; }
  public static IntPtr Child(IntPtr top, string text) { IntPtr f = IntPtr.Zero; EnumChildWindows(top, (h, l) => { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.ToString() == text && IsButton(h) && IsWindowVisible(h) && IsWindowEnabled(h)) { f = h; return false; } return true; }, IntPtr.Zero); return f; }
}
'@ }
$repo = Split-Path $PSScriptRoot -Parent
$setup = Join-Path $repo 'build\AI-Usage-Bar-Setup-0.1.0.exe'
$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$name, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Output "  PASS  $name" } else { $script:fail++; Write-Output "  FAIL  $name $detail" }
}
if (Get-Process -Name AIUsageBar -ErrorAction SilentlyContinue) { throw 'AI Usage Bar is running. Quit it first.' }

$root = Join-Path ([IO.Path]::GetTempPath()) ('aiub-setup-gui-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $root | Out-Null
$inst = Join-Path $root 'Programs\AI Usage Bar'
$exe = Join-Path $inst 'AIUsageBar.exe'
function Start-Direct([string]$file, [string]$arguments) {
    $psi = New-Object Diagnostics.ProcessStartInfo $file
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    return [Diagnostics.Process]::Start($psi)
}
function Find-Window([int]$procId, [int]$timeoutSec = 20) {
    $until = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $until) { $h = [GuiW]::TopWindow($procId); if ($h -ne [IntPtr]::Zero) { return $h }; Start-Sleep -Milliseconds 300 }
    return $null
}
function Find-Named($win, [string]$name, [int]$timeoutSec = 30) {
    $until = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $until) { $h = [GuiW]::Child($win, $name); if ($h -ne [IntPtr]::Zero) { return $h }; Start-Sleep -Milliseconds 300 }
    return $null
}
# BM_CLICK（ボタンを押す・チェックを切り替える）
function Click($h) { if ($null -eq $h) { throw 'control not found' }; [GuiW]::PostMessage($h, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; Start-Sleep -Milliseconds 300 }
function Toggle($h) { Click $h }

trap { Get-Process | Where-Object { $_.ProcessName -like 'AI-Usage-Bar-Setup*' -or $_.ProcessName -eq 'uninstall' -or ($_.ProcessName -eq 'AIUsageBar' -and $_.Path -like "$root*") } | ForEach-Object { $_.Kill() }; throw }
Write-Output '== 画面でインストール'
$p = Start-Direct $setup "/testroot:`"$root`""
$w = Find-Window $p.Id
Check ($null -ne $w) 'セットアップ画面が開く'
$desk = Find-Named $w 'デスクトップにショートカットを作る'
Toggle $desk
$btn = Find-Named $w 'インストール'
Check ($null -ne $btn) '「インストール」ボタンがある'
Click $btn
$done = Find-Named $w '完了' 60
Check ($null -ne $done) 'インストールが完了し「完了」ボタンに変わる'
Check ((Test-Path $exe) -and (Test-Path (Join-Path $root 'Desktop\AI Usage Bar.lnk')) -and (Test-Path (Join-Path $root 'StartMenu\AI Usage Bar.lnk'))) '本体・スタートメニュー・（選んだ）デスクトップのショートカット'
Start-Sleep -Seconds 4
$app = Get-Process -Name AIUsageBar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
Check ($null -ne $app) '「インストール後に起動する」でアプリが起動'
Click $done
Check ($p.WaitForExit(15000) -and $p.ExitCode -eq 0) '「完了」で閉じ、終了コード0' "exit=$($p.ExitCode)"

Write-Output '== 画面でアンインストール'
$data = Join-Path $root 'AIUsageBar'
New-Item -ItemType Directory $data -Force | Out-Null
Set-Content (Join-Path $data 'settings.json') '{}'
$u = Start-Direct (Join-Path $inst 'uninstall.exe') "/uninstall /testroot:`"$root`""
$w = Find-Window $u.Id
Check ($null -ne $w) 'アンインストール画面が開く'
Toggle (Find-Named $w '設定・集計データ・チームの参加情報も削除する')
Click (Find-Named $w 'アンインストール')
$done = Find-Named $w '完了' 60
Check ($null -ne $done) 'アンインストールが完了'
Click $done
Check ($u.WaitForExit(15000) -and $u.ExitCode -eq 0) '終了コード0'
Start-Sleep -Seconds 1
Check (-not (Get-Process -Name AIUsageBar -ErrorAction SilentlyContinue)) '起動中のアプリを終了させた'
Check (-not (Test-Path $inst) -and -not (Test-Path (Join-Path $root 'Desktop\AI Usage Bar.lnk')) -and -not (Test-Path (Join-Path $root 'StartMenu\AI Usage Bar.lnk')) -and -not (Test-Path $data)) '本体・ショートカット・（選んだ）データを削除'

Write-Output "結果: $($script:pass) passed, $($script:fail) failed"
if ($script:fail -gt 0) { exit 1 }
