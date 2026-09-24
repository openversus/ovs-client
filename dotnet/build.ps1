<#
.SYNOPSIS
Builds, tests and publishes the OpenVersus .NET client on Windows.

.DESCRIPTION
    dotnet\build.ps1                 build the libraries, run the tests, publish OpenVersus_<version>.asi
    dotnet\build.ps1 test            build and run the tests only
    dotnet\build.ps1 publish         publish OpenVersus_<version>.asi only
    dotnet\build.ps1 clean           remove every bin\ and obj\

Needs the .NET 10 SDK (https://dotnet.microsoft.com/download) and, for the publish, the
"Desktop development with C++" workload of Visual Studio or the Visual Studio Build Tools,
which NativeAOT uses to link. The tests need only the SDK.

.PARAMETER Rwx
Publish with trampoline pages read-write-execute for their whole life, as the C++ client did.

.PARAMETER Install
Copy the published OpenVersus_<version>.asi into this directory (the game's plugins folder),
renaming any OpenVersus*.asi already there to .bak, since the ASI loader would otherwise load both.

.PARAMETER SkipTests
Do not run the tests in the default command.
#>
[CmdletBinding()]
param(
    [ValidateSet("build", "test", "publish", "clean")]
    [string]$Command = "build",
    [switch]$Rwx,
    [string]$Install = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $here "OpenVersus.slnx"
$project = Join-Path $here "OpenVersus\OpenVersus.csproj"
$version = (Get-Content (Join-Path $here "..\VERSION") -Raw).Trim()
$published = Join-Path $here "OpenVersus\bin\Release\net10.0\win-x64\publish\OpenVersus_$version.asi"

function Say([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Error $text; exit 1 }

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { Fail "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

function Assert-DotnetSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail "dotnet is not on PATH; install the .NET 10 SDK from https://dotnet.microsoft.com/download"
    }
    $version = (& dotnet --version) -split '\.' | Select-Object -First 1
    if ([int]$version -lt 10) { Fail ".NET SDK 10 or newer is required (found $(& dotnet --version))" }
}

function Invoke-HostBuild {
    Say "building for the host"
    Invoke-Dotnet build $solution --nologo -v quiet
}

function Invoke-Tests {
    Say "running the tests"
    Invoke-Dotnet test $solution --nologo -v quiet
}

function Publish-Plugin {
    Say "publishing OpenVersus_$version.asi (NativeAOT, win-x64$(if ($Rwx) { ', RWX trampolines' }))"
    $properties = @()
    if ($Rwx) { $properties += "-p:RwxTrampolines=true" }
    Invoke-Dotnet publish $project -c Release -r win-x64 --nologo -v quiet @properties
    if (-not (Test-Path $published)) { Fail "publish finished but $published is missing" }
    Say "published $published ($([math]::Round((Get-Item $published).Length / 1MB, 1)) MB)"
    if ($Install) {
        if (-not (Test-Path $Install -PathType Container)) { Fail "$Install is not a directory" }
        foreach ($old in Get-ChildItem (Join-Path $Install "OpenVersus*.asi") -ErrorAction SilentlyContinue) {
            Move-Item $old.FullName "$($old.FullName).bak" -Force
            Write-Host "kept the previous plugin as $($old.FullName).bak"
        }
        Copy-Item $published $Install -Force
        Say "installed to $(Join-Path $Install (Split-Path $published -Leaf))"
    }
}

function Clear-BuildOutput {
    Say "removing bin\ and obj\"
    Get-ChildItem $here -Directory -Recurse -Include bin, obj | Remove-Item -Recurse -Force
}

Assert-DotnetSdk
switch ($Command) {
    "build" { Invoke-HostBuild; if (-not $SkipTests) { Invoke-Tests }; Publish-Plugin }
    "test" { Invoke-HostBuild; Invoke-Tests }
    "publish" { Publish-Plugin }
    "clean" { Clear-BuildOutput }
}

