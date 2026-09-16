[CmdletBinding()]
param(
    [string]$ExePath = "",
    [int]$StartupTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot "artifacts\portable-x64\Vanilla RTX App.exe"
}
$sourceExe = (Resolve-Path -LiteralPath $ExePath).Path

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("VanillaRTX-Portable-Smoke-" + [Guid]::NewGuid().ToString("N"))
$testExe = Join-Path $testRoot "Vanilla-RTX-App-Enhanced-Portable-x64.exe"
$crashLog = Join-Path $env:LOCALAPPDATA "Vanilla RTX App\LocalState\last_session_crash_log.txt"
$crashLogBefore = if (Test-Path -LiteralPath $crashLog) {
    (Get-Item -LiteralPath $crashLog).LastWriteTimeUtc
} else {
    [DateTime]::MinValue
}

$process = $null
$processStarted = $false
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Copy-Item -LiteralPath $sourceExe -Destination $testExe

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $testExe
    $startInfo.WorkingDirectory = Join-Path $env:WINDIR "Temp"
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables["PATH"] = "$env:WINDIR\System32;$env:WINDIR"
    foreach ($name in @("DOTNET_ROOT", "DOTNET_ROOT_X64", "MSBUILD_EXE_PATH", "VSINSTALLDIR")) {
        if ($startInfo.EnvironmentVariables.ContainsKey($name)) {
            $startInfo.EnvironmentVariables.Remove($name)
        }
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Process.Start returned false." }
    $processStarted = $true

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) {
            throw "The executable exited before showing its window (exit code $($process.ExitCode))."
        }
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "No main window appeared within $StartupTimeoutSeconds seconds."
    }
    if ($process.MainWindowTitle -ne "Vanilla RTX App") {
        throw "Unexpected window title '$($process.MainWindowTitle)'."
    }

    $modules = @($process.Modules | ForEach-Object {
        [pscustomobject]@{ Name = $_.ModuleName; Path = $_.FileName }
    })
    # The cache's app folder follows the distributed executable name, which this test
    # deliberately changes. Only the stable .net cache root can be asserted here.
    $bundleCacheFragment = "\.net\"
    $coreLibrary = @($modules | Where-Object { $_.Name -ieq "System.Private.CoreLib.dll" -and $_.Path -like "*$bundleCacheFragment*" })
    $winAppRuntime = @($modules | Where-Object { $_.Name -ieq "Microsoft.WindowsAppRuntime.dll" -and $_.Path -like "*$bundleCacheFragment*" })
    $appLocalVc = @($modules | Where-Object {
        $_.Name -match "^(vcruntime140|msvcp140|concrt140|vccorlib140).*\.dll$" -and
        $_.Path -like "*$bundleCacheFragment*\Runtimes\win-x64\vcredist\*"
    })

    if ($coreLibrary.Count -eq 0) { throw "System.Private.CoreLib.dll was not loaded from the single-file extraction cache." }
    if ($winAppRuntime.Count -eq 0) { throw "Microsoft.WindowsAppRuntime.dll was not loaded from the bundle." }
    if ($appLocalVc.Count -ne 10) { throw "Expected 10 app-local VC++ runtime modules; found $($appLocalVc.Count)." }

    $newCrashLog = (Test-Path -LiteralPath $crashLog) -and
        (Get-Item -LiteralPath $crashLog).LastWriteTimeUtc -gt $crashLogBefore
    if ($newCrashLog) { throw "A new startup crash log was generated at '$crashLog'." }

    if (-not $process.CloseMainWindow()) { throw "The main window rejected a normal close request." }
    if (-not $process.WaitForExit(15000)) { throw "The process did not close normally within 15 seconds." }
    if ($process.ExitCode -ne 0) { throw "The process closed with exit code $($process.ExitCode)." }

    [pscustomobject]@{
        Result = "PASS"
        Executable = $testExe
        WorkingDirectory = $startInfo.WorkingDirectory
        WindowTitle = "Vanilla RTX App"
        ExitCode = $process.ExitCode
        AppLocalVcModules = $appLocalVc.Count
        RuntimeSource = "single-file extraction cache"
    } | Format-List
}
finally {
    if ($processStarted -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
    if (Test-Path -LiteralPath $testRoot) {
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        if ($resolvedTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}
