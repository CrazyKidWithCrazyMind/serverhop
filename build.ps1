<#
.SYNOPSIS
    Builds SERVERHOP into a single self-contained Windows executable.

.DESCRIPTION
    Runs `dotnet publish` with single-file bundling so the output is one
    double-clickable ServerHop.exe (native libraries embedded for self-extraction).
    No .NET runtime, no install, no dependencies on the target machine.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER Runtime
    Target RID. Default: win-x64.

.PARAMETER Output
    Output folder. Default: .\publish next to this script.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64",
    [string]$Output        = ""
)

$ErrorActionPreference = "Stop"

# Resolve the script folder robustly ($PSScriptRoot can be empty in param defaults
# when invoked via `powershell -File`, e.g. by CI or a shell shortcut).
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Output) { $Output = Join-Path $root "publish" }

Write-Host "==> SERVERHOP publish ($Configuration / $Runtime)" -ForegroundColor Green

dotnet publish (Join-Path $root "ServerHop.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Get-Item (Join-Path $Output "ServerHop.exe")
$mb  = [math]::Round($exe.Length / 1MB, 1)

Write-Host "==> done: $($exe.FullName) ($mb MB)" -ForegroundColor Green
Write-Host "==> running self-test..." -ForegroundColor Green

$selftest = Start-Process -FilePath $exe.FullName -ArgumentList "--selftest" `
    -Wait -PassThru `
    -RedirectStandardOutput (Join-Path $Output "selftest.log") `
    -RedirectStandardError  (Join-Path $Output "selftest.err")

Get-Content (Join-Path $Output "selftest.log")

if ($selftest.ExitCode -ne 0) {
    throw "self-test FAILED (exit $($selftest.ExitCode))"
}

Write-Host "==> SELFTEST PASSED - ready to ship." -ForegroundColor Green
