[CmdletBinding()]
param(
    [string]$ExePath = "",
    [int]$StartupTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot "artifacts\portable-x64\Vanilla RTX App.exe"
}
$sourceExe = (Resolve-Path -LiteralPath $ExePath).Path

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds,
        [string]$Description
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($null -ne $value -and (($value -isnot [bool]) -or $value)) {
            return $value
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for $Description."
}

function Find-UiaElement {
    param(
        [int]$ProcessId,
        [string]$AutomationId = "",
        [string]$Name = ""
    )

    $conditions = [Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId))

    if (-not [string]::IsNullOrEmpty($AutomationId)) {
        $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId))
    }
    if (-not [string]::IsNullOrEmpty($Name)) {
        $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name))
    }

    $condition = [System.Windows.Automation.AndCondition]::new($conditions.ToArray())
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-UiaElement {
    param([System.Windows.Automation.AutomationElement]$Element)

    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Toggle-UiaElement {
    param([System.Windows.Automation.AutomationElement]$Element)

    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    ([System.Windows.Automation.TogglePattern]$pattern).Toggle()
}

function Get-ProcessForExecutable {
    param(
        [string]$ExecutablePath,
        [int]$ExcludeProcessId = 0
    )

    $resolvedPath = [IO.Path]::GetFullPath($ExecutablePath)
    foreach ($candidate in Get-Process -ErrorAction SilentlyContinue) {
        if ($candidate.Id -eq $ExcludeProcessId) { continue }
        try {
            if ([string]::Equals(
                    [IO.Path]::GetFullPath($candidate.Path),
                    $resolvedPath,
                    [StringComparison]::OrdinalIgnoreCase)) {
                $candidate
            }
        }
        catch {
            # Accessing Path for processes owned by other users can fail; they are irrelevant here.
        }
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("VanillaRTX-Language-Restart-" + [Guid]::NewGuid().ToString("N"))
$testExe = Join-Path $testRoot "Vanilla-RTX-App-Enhanced-Portable-x64.exe"
$storageRoot = Join-Path $testRoot "AppStorage"
$settingsPath = Join-Path $storageRoot "settings.json"
$crashLogPath = Join-Path $storageRoot "LocalState\last_session_crash_log.txt"
$firstProcess = $null
$replacementProcess = $null
$testStartedAt = [DateTime]::UtcNow

try {
    New-Item -ItemType Directory -Path $storageRoot -Force | Out-Null
    Copy-Item -LiteralPath $sourceExe -Destination $testExe

    # A persisted null optional setting reproduces the former serialization failure.
    # AppLanguage is deliberately absent so the test also verifies the System default.
    [IO.File]::WriteAllText(
        $settingsPath,
        '{"MinecraftPreviewInstallPath":{"T":null,"V":null}}',
        [Text.UTF8Encoding]::new($false))

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $testExe
    $startInfo.WorkingDirectory = Join-Path $env:WINDIR "Temp"
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables["PATH"] = "$env:WINDIR\System32;$env:WINDIR"
    $startInfo.EnvironmentVariables["VANILLA_RTX_APP_STORAGE_ROOT"] = $storageRoot
    foreach ($name in @("DOTNET_ROOT", "DOTNET_ROOT_X64", "MSBUILD_EXE_PATH", "VSINSTALLDIR")) {
        if ($startInfo.EnvironmentVariables.ContainsKey($name)) {
            $startInfo.EnvironmentVariables.Remove($name)
        }
    }

    $firstProcess = [Diagnostics.Process]::new()
    $firstProcess.StartInfo = $startInfo
    if (-not $firstProcess.Start()) { throw "Process.Start returned false." }

    $null = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the initial main window" -Condition {
        $firstProcess.Refresh()
        if ($firstProcess.HasExited) {
            throw "The initial process exited with code $($firstProcess.ExitCode)."
        }
        if ($firstProcess.MainWindowHandle -ne [IntPtr]::Zero) { return $true }
        return $null
    }

    $languageButton = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the language button" -Condition {
        Find-UiaElement -ProcessId $firstProcess.Id -AutomationId "LanguageButton"
    }
    Invoke-UiaElement -Element $languageButton

    $systemItem = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the System language option" -Condition {
        Find-UiaElement -ProcessId $firstProcess.Id -AutomationId "Language-System"
    }
    $systemToggle = [System.Windows.Automation.TogglePattern]$systemItem.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($systemToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        throw "A new profile did not default to System language detection."
    }

    $spanishItem = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the Spanish menu item" -Condition {
        Find-UiaElement -ProcessId $firstProcess.Id -AutomationId "Language-es-ES"
    }
    $expectedSpanishName = "Espa$([char]0x00F1)ol"
    if ($spanishItem.Current.Name -ne $expectedSpanishName) {
        throw "Expected the UTF-8 language label '$expectedSpanishName', got '$($spanishItem.Current.Name)'."
    }
    Toggle-UiaElement -Element $spanishItem

    $savedSettings = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the Spanish setting to be written" -Condition {
        if (-not (Test-Path -LiteralPath $settingsPath)) { return $null }
        try {
            $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            if ($settings.AppLanguage.T -eq "string" -and
                $settings.AppLanguage.V -eq "es-ES" -and
                $null -eq $settings.MinecraftPreviewInstallPath.T) {
                return $settings
            }
        }
        catch {
            # The atomic replace can briefly race this read; try again.
        }
        return $null
    }

    $restartButton = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the restart confirmation" -Condition {
        Find-UiaElement -ProcessId $firstProcess.Id -AutomationId "PrimaryButton"
    }
    Invoke-UiaElement -Element $restartButton

    if (-not $firstProcess.WaitForExit(15000)) {
        throw "The original process did not exit after restart was confirmed."
    }

    $replacementProcess = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the replacement process" -Condition {
        $candidates = @(Get-ProcessForExecutable -ExecutablePath $testExe -ExcludeProcessId $firstProcess.Id)
        if ($candidates.Count -gt 0) {
            return $candidates | Sort-Object StartTime -Descending | Select-Object -First 1
        }
        return $null
    }

    $null = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the restarted main window" -Condition {
        $replacementProcess.Refresh()
        if ($replacementProcess.HasExited) {
            throw "The replacement process exited with code $($replacementProcess.ExitCode)."
        }
        if ($replacementProcess.MainWindowHandle -ne [IntPtr]::Zero) { return $true }
        return $null
    }

    $null = Wait-Until -TimeoutSeconds $StartupTimeoutSeconds -Description "the Spanish restarted UI" -Condition {
        Find-UiaElement -ProcessId $replacementProcess.Id -Name "Seleccionar otros paquetes"
    }

    $settingsAfterRestart = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($settingsAfterRestart.AppLanguage.V -ne "es-ES") {
        throw "The replacement process did not preserve AppLanguage=es-ES."
    }

    if (-not $replacementProcess.CloseMainWindow()) {
        throw "The restarted process rejected a normal close request."
    }
    if (-not $replacementProcess.WaitForExit(15000)) {
        throw "The restarted process did not close normally."
    }
    $replacementExitCode = $null
    try {
        $replacementExitCode = $replacementProcess.ExitCode
    }
    catch {
        # A Process obtained through Get-Process is not always associated with an
        # exit-code handle, even after it has exited normally.
    }
    if ($null -ne $replacementExitCode -and $replacementExitCode -ne 0) {
        throw "The restarted process closed with exit code $replacementExitCode."
    }
    if (Test-Path -LiteralPath $crashLogPath) {
        throw "The language restart flow generated a crash log at '$crashLogPath'."
    }

    [pscustomobject]@{
        Result = "PASS"
        Executable = $testExe
        WorkingDirectory = $startInfo.WorkingDirectory
        DefaultLanguageMode = "System"
        MenuOpenedOnFirstInvoke = $true
        PersistedLanguage = $savedSettings.AppLanguage.V
        RestartedLanguage = $settingsAfterRestart.AppLanguage.V
        InitialProcessId = $firstProcess.Id
        RestartedProcessId = $replacementProcess.Id
        RestartedExitCode = $replacementExitCode
        DurationSeconds = [Math]::Round(([DateTime]::UtcNow - $testStartedAt).TotalSeconds, 1)
    } | Format-List
}
finally {
    foreach ($process in @($firstProcess, $replacementProcess)) {
        if ($null -eq $process) { continue }
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
        catch {
            # It may already have exited or been disposed after a successful test.
        }
    }

    foreach ($process in @(Get-ProcessForExecutable -ExecutablePath $testExe)) {
        try {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
        }
        catch {
            # Best-effort cleanup for an unexpected replacement process.
        }
    }

    if (Test-Path -LiteralPath $testRoot) {
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        if ($resolvedTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}
