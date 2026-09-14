[CmdletBinding()]
param([string]$Runtime = "win-x64")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\$Runtime"

dotnet publish (Join-Path $root "src\Vaguul.CodexAccountSwitcher\Vaguul.CodexAccountSwitcher.csproj") `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $output

$exe = Join-Path $output "Vaguul.CodexAccountSwitcher.exe"
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLowerInvariant()
"$hash  Vaguul.CodexAccountSwitcher.exe" | Set-Content -Encoding ascii (Join-Path $output "SHA256SUMS.txt")
Write-Host "Published: $exe"
Write-Host "SHA256:   $hash"
