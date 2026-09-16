[CmdletBinding()]
param(
    [string]$DotNetPath = "dotnet",
    [string]$OutputDirectory = "",
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourceProjectPath = Join-Path $repoRoot "src\Vanilla RTX App.csproj"
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactRoot "portable-x64"
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$artifactPrefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside '$artifactRoot'."
}

function Invoke-DotNet {
    param([string[]]$DotNetArguments)

    & $DotNetPath @DotNetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code ${LASTEXITCODE}: $($DotNetArguments -join ' ')"
    }
}

function Get-PeMachine {
    param([string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid DOS header." }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE header." }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$sdkVersionText = (& $DotNetPath --version).Trim()
if ($LASTEXITCODE -ne 0) { throw "Unable to execute '$DotNetPath'." }
$sdkVersion = [Version]($sdkVersionText.Split('-')[0])
if ($sdkVersion.Major -ne 10) {
    throw ".NET SDK 10 is required to build this net10.0 application; found $sdkVersionText."
}

if (-not (Test-Path -LiteralPath $sourceProjectPath)) {
    throw "Project file not found: '$sourceProjectPath'."
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

${stagingRoot} = Join-Path ([IO.Path]::GetTempPath()) ("VanillaRTX-Portable-Build-" + [Guid]::NewGuid().ToString("N"))
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

try {
    $robocopyArguments = @(
        $repoRoot, $stagingRoot,
        "/E",
        "/XD", ".git", ".vs", "bin", "obj", "artifacts",
        "/NFL", "/NDL", "/NJH", "/NJS", "/NP"
    )
    & robocopy @robocopyArguments | Out-Null
    $robocopyExitCode = $LASTEXITCODE
    if ($robocopyExitCode -ge 8) {
        throw "robocopy failed while preparing the clean build staging directory (exit code $robocopyExitCode)."
    }

    $projectPath = Join-Path $stagingRoot "src\Vanilla RTX App.csproj"
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "The clean build staging directory does not contain '$projectPath'."
    }

    Invoke-DotNet @(
        "clean", $projectPath,
        "-c", "Release",
        "-p:Platform=x64",
        "-r", "win-x64",
        "-v:minimal"
    )

    if ($NoRestore) {
        Write-Warning "-NoRestore is ignored for the isolated clean build; restore is required to create its dependency graph."
    }

    Invoke-DotNet @(
        "restore", $projectPath,
        "-r", "win-x64",
        "-p:Platform=x64",
        "-v:minimal"
    )

    $stagingOutputPath = Join-Path $stagingRoot "publish"
    Invoke-DotNet @(
        "publish", $projectPath,
        "-c", "Release",
        "-p:Platform=x64",
        "-p:PublishProfile=Portable-x64",
        "-p:ContinuousIntegrationBuild=true",
        "-o", $stagingOutputPath,
        "--no-restore",
        "-v:minimal"
    )

    $stagedFiles = @(Get-ChildItem -LiteralPath $stagingOutputPath -File -Recurse)
    if ($stagedFiles.Count -ne 1 -or $stagedFiles[0].Extension -ne ".exe") {
        $names = $stagedFiles.FullName -join [Environment]::NewLine
        throw "Clean staging publish must contain exactly one .exe. Found:$([Environment]::NewLine)$names"
    }

    Copy-Item -LiteralPath $stagedFiles[0].FullName -Destination (Join-Path $outputPath $stagedFiles[0].Name) -Force
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        try {
            $resolvedStagingRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $stagingRoot).Path)
            if ($resolvedStagingRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force
            }
            else {
                Write-Warning "Not removing unexpected staging path '$resolvedStagingRoot'."
            }
        }
        catch {
            Write-Warning "Could not remove clean build staging directory '$stagingRoot': $($_.Exception.Message)"
        }
    }
}

$publishedFiles = @(Get-ChildItem -LiteralPath $outputPath -File -Recurse)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Extension -ne ".exe") {
    $names = $publishedFiles.FullName -join [Environment]::NewLine
    throw "Portable publish must contain exactly one .exe. Found:$([Environment]::NewLine)$names"
}

$exe = $publishedFiles[0]
$machine = Get-PeMachine -Path $exe.FullName
if ($machine -ne 0x8664) {
    throw ("Expected an x64 PE (0x8664), found 0x{0:X4}." -f $machine)
}

$hash = Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256
$signature = Get-AuthenticodeSignature -LiteralPath $exe.FullName

Write-Host "Portable publish validated."
Write-Host "Executable : $($exe.FullName)"
Write-Host "Size       : $($exe.Length) bytes"
Write-Host "Architecture: x64"
Write-Host "SHA-256    : $($hash.Hash)"
Write-Host "Signature  : $($signature.Status)"

if ($signature.Status -eq "NotSigned") {
    Write-Warning "The executable is portable but not Authenticode-signed; SmartScreen reputation warnings remain possible."
}
