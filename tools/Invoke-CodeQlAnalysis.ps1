#Requires -Version 5.1
<#
.SYNOPSIS
    Runs a local traced C# CodeQL analysis for this repository.

.DESCRIPTION
    Creates a CodeQL database by tracing the repository's Release x64 restore and
    build, then analyzes that database with the security-extended and
    security-and-quality suites from the CodeQL CLI already installed on the machine.

    The query suites are resolved from that CLI. This script does not download
    query packs. Evidence is written under .artifacts/codeql/, which Git ignores.
    A successful database build is not treated as a finished analysis: the script
    also requires database analyze to exit 0.

    The repository CodeQL config is passed at database creation. For a traced C#
    build, the CLI records that paths and paths-ignore do not filter compiled files.

.PARAMETER CodeQl
    Path to codeql.exe. Defaults to C:\Tools\codeql\codeql.exe.
#>
[CmdletBinding()]
param(
    [string] $CodeQl = "C:\Tools\codeql\codeql.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CodeQl {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $LogPath
    )

    # Run through cmd so CodeQL's stderr progress is captured in the log instead of
    # becoming a PowerShell error record. The cmd exit code is the CodeQL exit code.
    $renderedArguments = foreach ($argument in $Arguments) {
        if ($argument -match '[\s"]') {
            '"' + ($argument -replace '"', '\"') + '"'
        }
        else {
            $argument
        }
    }

    $commandLine = "`"$CodeQl`" $($renderedArguments -join ' ') > `"$LogPath`" 2>&1"
    & cmd.exe /c $commandLine
    $exitCode = $LASTEXITCODE
    [System.IO.File]::AppendAllText($LogPath, "EXIT_CODE=$exitCode$([Environment]::NewLine)")
    if ($exitCode -ne 0) {
        throw "codeql $($Arguments[0]) $($Arguments[1]) exited with code $exitCode. See $LogPath"
    }
}

function Get-InstalledPackRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackName,

        [Parameter(Mandatory = $true)]
        $Node
    )

    if ($null -eq $Node) {
        return $null
    }

    if ($Node -is [System.Array]) {
        foreach ($item in $Node) {
            $found = Get-InstalledPackRoot -PackName $PackName -Node $item
            if ($found) {
                return $found
            }
        }

        return $null
    }

    if ($Node -isnot [System.Management.Automation.PSCustomObject]) {
        return $null
    }

    foreach ($property in $Node.PSObject.Properties) {
        if ($property.Name -eq $PackName) {
            $entry = $property.Value
            if ($entry -is [System.Array]) {
                $entry = $entry | Where-Object { $_.PSObject.Properties.Name -contains "path" } | Select-Object -First 1
            }

            if ($entry -and ($entry.PSObject.Properties.Name -contains "path") -and $entry.path) {
                return Split-Path -Parent $entry.path
            }
        }

        $nested = Get-InstalledPackRoot -PackName $PackName -Node $property.Value
        if ($nested) {
            return $nested
        }
    }

    return $null
}

if (-not (Test-Path -LiteralPath $CodeQl)) {
    throw "CodeQL CLI was not found at $CodeQl"
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$evidenceRoot = Join-Path $repoRoot ".artifacts\codeql"
$database = Join-Path $evidenceRoot "csharp"
$config = Join-Path $repoRoot ".github\codeql\codeql-config.yml"
$buildCommand = Join-Path $evidenceRoot "build.cmd"
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

if (-not (Test-Path -LiteralPath $config)) {
    throw "CodeQL configuration was not found at $config"
}

@(
    "@echo off"
    "dotnet restore BackFiller.slnx -p:Platform=x64"
    "if errorlevel 1 exit /b %errorlevel%"
    "dotnet build BackFiller.slnx --configuration Release -p:Platform=x64 --no-restore"
    "if errorlevel 1 exit /b %errorlevel%"
) | Set-Content -LiteralPath $buildCommand -Encoding ascii

$previousErrorAction = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    & $CodeQl version | Set-Content -LiteralPath (Join-Path $evidenceRoot "codeql-version.txt") -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "codeql version exited with code $LASTEXITCODE"
    }

    $resolveLog = Join-Path $evidenceRoot "resolve-packs.json"
    & $CodeQl resolve packs --format=json | Set-Content -LiteralPath $resolveLog -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "codeql resolve packs exited with code $LASTEXITCODE. See $resolveLog"
    }
}
finally {
    $ErrorActionPreference = $previousErrorAction
}

$packs = Get-Content -Raw -LiteralPath $resolveLog | ConvertFrom-Json
$queriesRoot = Get-InstalledPackRoot -PackName "codeql/csharp-queries" -Node $packs
if (-not $queriesRoot) {
    throw "The installed CodeQL CLI does not contain codeql/csharp-queries. See $resolveLog"
}

$suites = @(
    (Join-Path $queriesRoot "codeql-suites\csharp-security-extended.qls")
    (Join-Path $queriesRoot "codeql-suites\csharp-security-and-quality.qls")
)
foreach ($suite in $suites) {
    if (-not (Test-Path -LiteralPath $suite)) {
        throw "Query suite was not found at $suite"
    }
}

if (Test-Path -LiteralPath $database) {
    Remove-Item -LiteralPath $database -Recurse -Force
}

Push-Location $repoRoot
try {
    Invoke-CodeQl -LogPath (Join-Path $evidenceRoot "database-create.log") -Arguments @(
        "database", "create", $database,
        "--language=csharp",
        "--source-root", $repoRoot,
        "--threads=0",
        "--codescanning-config", $config,
        "--command", $buildCommand
    )

    Invoke-CodeQl -LogPath (Join-Path $evidenceRoot "database-analyze.log") -Arguments (@(
        "database", "analyze", $database
    ) + $suites + @(
        "--format=sarifv2.1.0",
        "--output", (Join-Path $evidenceRoot "results.sarif"),
        "--threads=0",
        "--no-sarif-minify",
        "--sarif-category=csharp"
    ))
}
finally {
    Pop-Location
}
