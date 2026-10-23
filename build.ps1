# AI Usage Bar (Windows) のビルド。Windows標準の .NET Framework 4.8 コンパイラだけを使う。
#   .\build.ps1             tests.exe・AIUsageBar.exe・セットアップをビルド
#   .\build.ps1 -TestsOnly  tests.exe だけ
#   .\build.ps1 -SetupOnly  セットアップだけ（AIUsageBar.exe は作り直さない。仕様書に exe のハッシュを書いた後に使う）
param([switch]$TestsOnly, [switch]$SetupOnly)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force $out | Out-Null

$core = @('Json.cs','Common.cs','Hardening.cs','Policy.cs','CodexClient.cs','LineReader.cs','LocalLogs.cs','ClaudeUsage.cs','UsageStore.cs','Activity.cs','SecurityInfo.cs','TeamCrypto.cs','Team.cs','Mailer.cs','AiClient.cs','TeamAnalytics.cs','TeamDashboard.cs','Friends.cs','ResetWatch.cs') |
    ForEach-Object { Join-Path $root "src\$_" } | Where-Object { Test-Path $_ }
$ui = @('Theme.cs','TrayIcons.cs','TeamUi.cs','FriendsUi.cs','Dashboard.cs','TrayApp.cs','Program.cs') |
    ForEach-Object { Join-Path $root "src\$_" } | Where-Object { Test-Path $_ }
$refs = @('-r:System.dll','-r:System.Windows.Forms.dll','-r:System.Drawing.dll','-r:System.Security.dll')
$common = @('-nologo','-codepage:65001','-utf8output','-unsafe','-optimize+','-platform:anycpu','-highentropyva+','-langversion:5') + $refs
$icon = Join-Path $root 'assets\AppIcon.ico'
$iconArg = @(); if (Test-Path $icon) { $iconArg = @("-win32icon:$icon") }

if (-not $SetupOnly) {
    & $csc @common '-target:exe' "-out:$out\tests.exe" @core @ui (Join-Path $root 'tests\Tests.cs') (Join-Path $root 'tests\Compat.cs') (Join-Path $root 'tests\TeamTests.cs') (Join-Path $root 'tests\TeamRender.cs') (Join-Path $root 'tests\TeamDemo.cs') (Join-Path $root 'tests\MacBridge.cs') (Join-Path $root 'tests\AiCheck.cs') (Join-Path $root 'tests\FriendsTests.cs') (Join-Path $root 'tests\ResetTests.cs') '-main:AIUsageBar.Tests.TestMain'
    if ($LASTEXITCODE -ne 0) { throw 'tests build failed' }
    if ($TestsOnly) { return }

    $manifest = Join-Path $root 'assets\app.manifest'
    & $csc @common '-target:winexe' "-out:$out\AIUsageBar.exe" "-win32manifest:$manifest" @iconArg @core @ui '-main:AIUsageBar.Program'
    if ($LASTEXITCODE -ne 0) { throw 'app build failed' }
}

# Setup: embeds the app exe, its SHA-256 and the three documents (readme, spec, checklist).
$setupName = 'AI-Usage-Bar-Setup-0.1.0.exe'
$appHash = (Get-FileHash "$out\AIUsageBar.exe" -Algorithm SHA256).Hash
[IO.File]::WriteAllText("$out\AIUsageBar.exe.sha256", $appHash, [Text.Encoding]::ASCII)
$readme = (Get-ChildItem $root -Filter 'AI-Usage-Bar-Windows-*.txt' | Select-Object -First 1).FullName
$spec = Join-Path $root 'docs\仕様書・セキュリティ資料.md'
$checklist = (Get-ChildItem (Join-Path $root 'docs') -Filter '*.txt' | Select-Object -First 1).FullName
$res = @("-resource:$out\AIUsageBar.exe,AIUsageBar.exe", "-resource:$out\AIUsageBar.exe.sha256,AIUsageBar.exe.sha256",
    "-resource:$readme,readme.txt", "-resource:$spec,spec.md", "-resource:$checklist,checklist.txt")
& $csc @common '-target:winexe' "-out:$out\$setupName" "-win32manifest:$root\installer\setup.manifest" @iconArg @res (Join-Path $root 'installer\Setup.cs')
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }
Get-Item "$out\AIUsageBar.exe", "$out\$setupName" | Select-Object Name, Length, LastWriteTime
