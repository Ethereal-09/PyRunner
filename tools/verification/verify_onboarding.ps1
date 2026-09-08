param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OnboardingNativeMethods {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
}
'@

$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsDirectory = Join-Path $solutionRoot 'artifacts'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$dataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "PyRunner-Onboarding-E2E-$runId"
$screenshotDirectory = Join-Path $artifactsDirectory "onboarding-final-$runId"
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $screenshotDirectory -Force | Out-Null
$scriptFixtureDirectory = Join-Path $dataDirectory 'selected-scripts'
$excludedFixtureDirectory = Join-Path $scriptFixtureDirectory 'venv'
New-Item -ItemType Directory -Path $excludedFixtureDirectory -Force | Out-Null
Set-Content -LiteralPath (Join-Path $scriptFixtureDirectory 'hello.py') -Value 'print("PyRunner onboarding verification")' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $excludedFixtureDirectory 'ignored.py') -Value 'raise RuntimeError("excluded")' -Encoding UTF8

$executable = (Resolve-Path -LiteralPath (Join-Path $solutionRoot "PyRunner\bin\x64\$Configuration\net8.0-windows10.0.19041.0\PyRunner.exe")).Path
$previousDataDirectory = [Environment]::GetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', 'Process')
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $dataDirectory, 'Process')
$process = Start-Process -FilePath $executable -PassThru
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $previousDataDirectory, 'Process')

function Get-ProcessWindows {
    param([int]$ProcessId)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Wait-ForWindow {
    param([string]$AutomationId, [int]$TimeoutSeconds = 20)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($candidate in (Get-ProcessWindows -ProcessId $process.Id)) {
            if ([string]::IsNullOrEmpty($AutomationId) -or $candidate.Current.AutomationId -eq $AutomationId) {
                return $candidate
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for window '$AutomationId'."
}

function Wait-ForWindowContaining {
    param([string]$AutomationId, [int]$TimeoutSeconds = 25)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($candidate in $windows) {
            $candidateId = $candidate.Current.ProcessId
            if ($null -eq $candidateId -or $candidateId -le 0) { continue }
            $candidateProcess = Get-Process -Id $candidateId -ErrorAction SilentlyContinue
            if ($null -eq $candidateProcess -or $candidateProcess.ProcessName -ne 'PyRunner') { continue }
            if ($null -ne $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
                return $candidate
            }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for a PyRunner window containing '$AutomationId'."
}

function Find-Control {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutSeconds = 10
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $control = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $control) { return $control }
        Start-Sleep -Milliseconds 150
    }
    throw "Control '$AutomationId' was not found."
}

function Find-ControlByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [int]$TimeoutSeconds = 10
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $control = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $control) { return $control }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Invoke-Control {
    param([System.Windows.Automation.AutomationElement]$Control)
    $pattern = $Control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 700
}

function Save-WindowScreenshot {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$FileName
    )
    $bounds = $Window.Current.BoundingRectangle
    if ($bounds.Width -lt 100 -or $bounds.Height -lt 100) { throw 'Window bounds are invalid.' }
    $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $deviceContext = $graphics.GetHdc()
        try {
            $handle = [IntPtr]$Window.Current.NativeWindowHandle
            [OnboardingNativeMethods]::SetForegroundWindow($handle) | Out-Null
            Start-Sleep -Milliseconds 250
            if (-not [OnboardingNativeMethods]::PrintWindow($handle, $deviceContext, 2)) {
                throw "PrintWindow failed for $FileName."
            }
        }
        finally { $graphics.ReleaseHdc($deviceContext) }
        $path = Join-Path $screenshotDirectory $FileName
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        return $path
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$results = [ordered]@{}
$mainWindow = $null
try {
    $window = Wait-ForWindowContaining -AutomationId 'OnboardingNextButton'
    $results.InitialWindowName = $window.Current.Name
    Start-Sleep -Seconds 1
    $results.InitialTopLevelWindowCount = (Get-ProcessWindows -ProcessId $window.Current.ProcessId).Count
    $results.WelcomeScreenshot = Save-WindowScreenshot -Window $window -FileName '01-welcome.png'

    Invoke-Control (Find-Control -Root $window -AutomationId 'OnboardingNextButton')
    $pathInput = Find-Control -Root $window -AutomationId 'OnboardingPathInput'
    $valuePattern = $pathInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($scriptFixtureDirectory)
    Invoke-Control (Find-Control -Root $window -AutomationId 'OnboardingAddPathButton')
    $results.PathsScreenshot = Save-WindowScreenshot -Window $window -FileName '02-script-folders.png'

    Invoke-Control (Find-Control -Root $window -AutomationId 'OnboardingNextButton')
    Start-Sleep -Seconds 3
    $results.InterpreterScreenshot = Save-WindowScreenshot -Window $window -FileName '03-interpreter.png'

    Invoke-Control (Find-Control -Root $window -AutomationId 'OnboardingNextButton')
    $results.CompleteScreenshot = Save-WindowScreenshot -Window $window -FileName '04-complete.png'

    Invoke-Control (Find-Control -Root $window -AutomationId 'OnboardingBackButton')
    $null = Find-Control -Root $window -AutomationId 'OnboardingNextButton'
    Invoke-Control (Find-Control -Root $window -AutomationId 'OnboardingNextButton')

    Invoke-Control (Find-Control -Root $window -AutomationId 'OnboardingCenterStartButton')
    $settingsPath = Join-Path $dataDirectory 'settings.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while (-not (Test-Path -LiteralPath $settingsPath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $results.FirstRunCompleted = [bool]$settings.FirstRunCompleted
    $results.FirstRunVersion = [int]$settings.FirstRunVersion
    $results.AutoRefreshScripts = [bool]$settings.AutoRefreshScripts

    if ($results.InitialTopLevelWindowCount -ne 1) { throw 'Main window or another top-level window existed during onboarding.' }
    if (-not $results.FirstRunCompleted -or $results.FirstRunVersion -ne 1) { throw 'Completion state was not persisted correctly.' }

    # 重新使用同一数据目录启动，验证 completed/version 门控会直接进入主窗口，且引导不再出现。
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $dataDirectory, 'Process')
    $process = Start-Process -FilePath $executable -PassThru
    [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $previousDataDirectory, 'Process')
    $mainWindow = Wait-ForWindow -AutomationId '' -TimeoutSeconds 25
    $null = Find-Control -Root $mainWindow -AutomationId 'TitleBarThemeButton' -TimeoutSeconds 20
    $results.FinalTopLevelWindowCount = (Get-ProcessWindows -ProcessId $mainWindow.Current.ProcessId).Count
    $results.SelectedDirectoryImported = $null -ne (Find-ControlByName -Root $mainWindow -Name 'hello.py' -TimeoutSeconds 12)
    $results.MainScreenshot = Save-WindowScreenshot -Window $mainWindow -FileName '05-main-window.png'
    if ($results.FinalTopLevelWindowCount -ne 1) { throw 'Completed startup did not produce exactly one main window.' }
    if (-not $results.SelectedDirectoryImported) { throw 'The selected directory script was not imported.' }

    $results.Verified = $true
    $results | ConvertTo-Json -Depth 3
}
finally {
    if ($null -ne $mainWindow) {
        try {
            $windowPattern = $mainWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $windowPattern.Close()
        }
        catch { }
    }
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    if ($dataDirectory.StartsWith([System.IO.Path]::GetTempPath(), [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
