# セットアップの自動テスト。実際のスタートメニュー・「アプリと機能」・自動起動の登録は変更しない
# （/testroot で一時フォルダと HKCU\Software\AIUsageBarSetupTest に差し替える）。
#   pwsh -File installer\test-setup.ps1
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$build = Join-Path $repo 'build'
$setupSrc = Join-Path $build 'AI-Usage-Bar-Setup-0.1.0.exe'
$appHash = (Get-FileHash (Join-Path $build 'AIUsageBar.exe') -Algorithm SHA256).Hash
$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$name, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Output "  PASS  $name" } else { $script:fail++; Write-Output "  FAIL  $name $detail" }
}

# エラーダイアログを出さない（子プロセスに引き継がれる）
if (-not ('ErrMode' -as [type])) { Add-Type 'using System.Runtime.InteropServices; public static class ErrMode { [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint m); }' }
[ErrMode]::SetErrorMode(0x8003) | Out-Null

if (Get-Process -Name AIUsageBar -ErrorAction SilentlyContinue) { throw 'AI Usage Bar is running. Quit it before running this test (the test sends it a quit request).' }

$root = Join-Path ([IO.Path]::GetTempPath()) ('aiub-setup-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$dl = Join-Path $root 'ダウンロード 受信'
New-Item -ItemType Directory $dl -Force | Out-Null
$setup = Join-Path $dl 'AI-Usage-Bar-Setup-0.1.0.exe'
Copy-Item $setupSrc $setup
# インターネットから受け取ったファイルと同じ印を付ける
Set-Content -Path $setup -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=https://files.slack.com/"
$tr = Join-Path $root 'env'
$inst = Join-Path $tr 'Programs\AI Usage Bar'
$exe = Join-Path $inst 'AIUsageBar.exe'
$uninst = Join-Path $inst 'uninstall.exe'
$startLink = Join-Path $tr 'StartMenu\AI Usage Bar.lnk'
$deskLink = Join-Path $tr 'Desktop\AI Usage Bar.lnk'
$sha = [Security.Cryptography.SHA256]::Create()
$id = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($tr.ToLowerInvariant())), 0, 6).Replace('-', '')
$regBase = "HKCU:\Software\AIUsageBarSetupTest\$id"
$regUninst = "$regBase\Uninstall\AIUsageBar"
$regRun = "$regBase\Run"
$wsh = New-Object -ComObject WScript.Shell

# ShellExecute（ダブルクリック相当）で起動すると「セキュリティの警告」画面が出て止まるため、
# 一括配布ツールと同じく CreateProcess で起動する
function Start-Direct([string]$file, [string[]]$argList) {
    $psi = New-Object Diagnostics.ProcessStartInfo $file
    $psi.Arguments = ($argList -join ' ')
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path $file -Parent
    return [Diagnostics.Process]::Start($psi)
}
function Run([string]$file, [string[]]$argList, [int]$timeoutSec = 90) {
    $p = Start-Direct $file $argList
    if (-not $p.WaitForExit($timeoutSec * 1000)) { try { $p.Kill() } catch {}; return -999 }
    return $p.ExitCode
}
function RunValue { try { (Get-ItemProperty -Path $regRun -Name AIUsageBar -ErrorAction Stop).AIUsageBar } catch { $null } }
$T = "/testroot:`"$tr`""

# 実際の登録先（テストの前後で変わらないこと）
$realLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'AI Usage Bar.lnk'
$realKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AIUsageBar'
$realRun = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).AIUsageBar
$realBefore = @((Test-Path $realLink), (Test-Path $realKey), $realRun)

Write-Output '== 引数'
Check ((Run $setup @('/quiet', '/unknown')) -eq 2) '不明な指定は終了コード2'
Check ((Run $setup @('/quiet', '/testroot:C:\Windows')) -eq 2) '検査用の差し替え先は一時フォルダの中だけ'

Write-Output '== 無音インストール'
$log = Join-Path $root 'setup.log'
Check ((Run $setup @('/quiet', $T, "/log:`"$log`"")) -eq 0) '終了コード0'
Check ((Test-Path $exe) -and (Get-FileHash $exe -Algorithm SHA256).Hash -eq $appHash) '本体をコピーし、SHA-256 がビルドと一致'
Check ((@('はじめに.txt', '仕様書・セキュリティ資料.md', '他のPCでの確認手順.txt', 'uninstall.exe') | ForEach-Object { Test-Path (Join-Path $inst $_) }) -notcontains $false) '説明書3点とアンインストーラーを配置'
Check (-not (Get-Item $exe -Stream Zone.Identifier -ErrorAction SilentlyContinue)) 'インストールした本体には「インターネットから来た」印が付かない（起動のたびに警告が出ない）'
Check ((Test-Path $startLink) -and $wsh.CreateShortcut($startLink).TargetPath -eq $exe) 'スタートメニューのショートカットが本体を指す'
Check (-not (Test-Path $deskLink)) '無音インストールの既定ではデスクトップにショートカットを作らない'
$u = Get-ItemProperty $regUninst -ErrorAction SilentlyContinue
Check ($u -and $u.DisplayName -eq 'AI Usage Bar' -and $u.DisplayVersion -eq '0.1.0' -and $u.UninstallString -like "`"$uninst`" /uninstall*" -and $u.QuietUninstallString -like '*/quiet' -and $u.NoModify -eq 1) '「アプリと機能」の登録（表示名・版・アンインストール方法）'
Check ($null -eq (RunValue)) '既定ではサインイン時の起動を登録しない'
Check ((Test-Path $log) -and (Get-Content $log -Raw) -match 'インストール完了') 'ログを書き出す'
Check (-not (Get-Process -Name AIUsageBar -ErrorAction SilentlyContinue)) '無音インストールの既定では起動しない'

Write-Output '== 上書きインストール（オプション付き）'
Check ((Run $setup @('/quiet', '/desktop', '/startup', $T)) -eq 0) '終了コード0'
Check ((Test-Path $deskLink) -and $wsh.CreateShortcut($deskLink).TargetPath -eq $exe) '/desktop でデスクトップにショートカット'
Check ((RunValue) -eq "`"$exe`"") '/startup でサインイン時の起動を登録（アプリの設定と同じ形式）'

Write-Output '== 起動中のアプリがある場合'
$app = Start-Direct $exe @()
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $app.Refresh(); if ($app.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline.AddSeconds(-12)) { break } }
Start-Sleep -Seconds 3
$app.Refresh()
Check (-not $app.HasExited) '（準備）インストールしたアプリが起動した'
$sw = [Diagnostics.Stopwatch]::StartNew()
$code = Run $setup @('/quiet', $T)
$sw.Stop()
$app.Refresh()
Check ($code -eq 0 -and $app.HasExited) 'セットアップがアプリに終了を依頼し、上書きできた' "code=$code exited=$($app.HasExited)"
Check ($app.HasExited -and $app.ExitCode -eq 0) 'アプリは強制終了ではなく正常に終了した' "exit=$($app.ExitCode)"
Write-Output ("  （所要 {0:N1} 秒）" -f $sw.Elapsed.TotalSeconds)

Write-Output '== 組織ポリシー'
Remove-ItemProperty -Path $regRun -Name AIUsageBar
New-Item -Path "$regBase\Policies" -Force | Out-Null
New-ItemProperty -Path "$regBase\Policies" -Name DisableStartupRegistration -Value 1 -PropertyType DWord -Force | Out-Null
Check ((Run $setup @('/quiet', '/startup', $T)) -eq 0 -and $null -eq (RunValue)) 'DisableStartupRegistration=1 ならサインイン時の起動を登録しない'
Remove-Item -Path "$regBase\Policies" -Recurse
Check ((Run $setup @('/quiet', '/startup', $T)) -eq 0 -and (RunValue) -eq "`"$exe`"") '（準備）もう一度登録'

Write-Output '== 無音アンインストール（データは残す）'
$data = Join-Path $tr 'AIUsageBar'
New-Item -ItemType Directory $data -Force | Out-Null
Set-Content (Join-Path $data 'settings.json') '{}'
Set-Content (Join-Path $inst '利用者のメモ.txt') 'user file'
Check ((Run $uninst @('/uninstall', '/quiet', $T)) -eq 0) '終了コード0'
Check (-not (Test-Path $exe) -and -not (Test-Path $uninst)) '本体とアンインストーラーを削除（実行中のアンインストーラーは一時フォルダへ移す）'
Check (Test-Path (Join-Path $inst '利用者のメモ.txt')) 'セットアップが置いていないファイルは消さない'
Check (-not (Test-Path $startLink) -and -not (Test-Path $deskLink)) 'ショートカットを削除'
Check (-not (Test-Path $regUninst)) '「アプリと機能」の登録を削除'
Check ($null -eq (RunValue)) 'サインイン時の起動の登録を削除'
Check (Test-Path (Join-Path $data 'settings.json')) '既定では設定・データを残す'
Remove-Item (Join-Path $inst '利用者のメモ.txt')

Write-Output '== 別の場所を指すショートカット・データの削除'
Check ((Run $setup @('/quiet', '/desktop', $T)) -eq 0) '（準備）再インストール'
$foreign = $wsh.CreateShortcut($deskLink); $foreign.TargetPath = Join-Path $env:WINDIR 'notepad.exe'; $foreign.Save()
Check ((Run $uninst @('/uninstall', '/quiet', '/removedata', $T)) -eq 0) '/removedata 付きで終了コード0'
Check (Test-Path $deskLink) '同じ名前でも別のプログラムを指すショートカットは消さない'
Check (-not (Test-Path $data)) '/removedata で設定・データも削除'
Check (-not (Test-Path $inst)) 'インストール先フォルダが空になれば削除'

Write-Output '== DLL の植え込み'
$plant = Join-Path $root 'plant'
New-Item -ItemType Directory $plant | Out-Null
Copy-Item $setup $plant
foreach ($n in 'version.dll','dwmapi.dll','uxtheme.dll','CRYPTSP.dll','CRYPTBASE.dll','profapi.dll','wtsapi32.dll','userenv.dll','SspiCli.dll','windows.storage.dll','propsys.dll','shfolder.dll','mscoree.dll','clrjit.dll','netutils.dll','srvcli.dll','urlmon.dll','iertutil.dll','ole32.dll','comctl32.dll') {
    [IO.File]::WriteAllBytes((Join-Path $plant $n), [byte[]](1..64))
}
$code = Run (Join-Path $plant 'AI-Usage-Bar-Setup-0.1.0.exe') @('/quiet', $T)
Check ($code -eq 0 -and (Test-Path $exe)) 'セットアップと同じフォルダに偽の DLL が20個あってもインストールできる' "code=$code"
Check ((Run $uninst @('/uninstall', '/quiet', '/removedata', $T)) -eq 0) '（後片付け）アンインストール'

Write-Output '== 画面'
if ($OutDir) {
    Check ((Run $setup @("/render:`"$OutDir`"")) -eq 0 -and (Test-Path (Join-Path $OutDir 'setup-install.png'))) '画面の見本を出力'
}

$realAfter = @((Test-Path $realLink), (Test-Path $realKey), (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).AIUsageBar)
Check (($realBefore -join '|') -eq ($realAfter -join '|')) '実際のスタートメニュー・「アプリと機能」・自動起動の登録は変わっていない'

Write-Output ''
Write-Output "テスト用フォルダ：$root"
Write-Output "テスト用レジストリ：HKCU\Software\AIUsageBarSetupTest\$id"
Write-Output "結果: $($script:pass) passed, $($script:fail) failed"
if ($script:fail -gt 0) { exit 1 }
