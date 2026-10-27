$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'dist/windows'
$stage = Join-Path $repo 'build/windows-package/AI-Usage-Bar-Windows-0.1.0'
if (Test-Path -LiteralPath $stage) { throw 'Use a fresh staging directory.' }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'build/AI-Usage-Bar-Setup-0.1.0.exe') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'docs/windows-install.md') -Destination (Join-Path $stage 'README.md')
$revision = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source revision' }
Set-Content -LiteralPath (Join-Path $stage 'SOURCE.txt') -Encoding utf8 -Value "https://github.com/noah-traderlab/ai-usage-bar/tree/$revision"
$setup = Join-Path $stage 'AI-Usage-Bar-Setup-0.1.0.exe'
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding ascii -Value "$setupHash  AI-Usage-Bar-Setup-0.1.0.exe"
$zip = Join-Path $out 'AI-Usage-Bar-Windows-0.1.0.zip'
Compress-Archive -LiteralPath $stage -DestinationPath $zip
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii -Value "$zipHash  AI-Usage-Bar-Windows-0.1.0.zip"
