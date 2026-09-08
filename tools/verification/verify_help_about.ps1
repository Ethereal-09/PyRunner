param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HelpUiNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
}
'@

$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsDirectory = Join-Path $solutionRoot 'artifacts'
$screenshotDirectory = Join-Path $artifactsDirectory "help-ui-check-$(Get-Date -Format 'yyyyMMdd-HHmmssfff')"
New-Item -ItemType Directory -Path $screenshotDirectory -Force | Out-Null
$dataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "PyRunner-HelpAbout-$PID-$(Get-Date -Format 'yyyyMMddHHmmssfff')"
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
@{ FirstRunCompleted = $true; FirstRunVersion = 1; Theme = 'Light' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataDirectory 'settings.json') -Encoding utf8
$executable = Join-Path $solutionRoot "PyRunner\bin\x64\$Configuration\net8.0-windows10.0.19041.0\PyRunner.exe"
$lightScreenshot = Join-Path $screenshotDirectory 'help-about-light.png'
$darkScreenshot = Join-Path $screenshotDirectory 'help-about-dark.png'
$narrowScreenshot = Join-Path $screenshotDirectory 'help-about-narrow-dark.png'

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
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Control not found: $AutomationId"
}

function Invoke-Control {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Save-WindowScreenshot {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Path
    )
    $bounds = $Window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$bounds.Left, [int]$bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$previousDataDirectory = [Environment]::GetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', 'Process')
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $dataDirectory, 'Process')
$process = Start-Process -FilePath $executable -PassThru
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $previousDataDirectory, 'Process')

try {
    $window = $null
    for ($attempt = 0; $attempt -lt 60 -and $null -eq $window; $attempt++) {
        Start-Sleep -Milliseconds 250
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
    }
    if ($null -eq $window) { throw 'PyRunner window was not found.' }

    [HelpUiNative]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
    Invoke-Control (Find-Control -Root $window -AutomationId 'NavHelpButton')
    $null = Find-Control -Root $window -AutomationId 'HelpAboutPage'
    $null = Find-Control -Root $window -AutomationId 'HelpAddFolderButton'
    $null = Find-Control -Root $window -AutomationId 'HelpInterpreterButton'

    Invoke-Control (Find-Control -Root $window -AutomationId 'HelpAddFolderButton')
    $folderTarget = Find-Control -Root $window -AutomationId 'SettingsAddFolderButton'
    Start-Sleep -Milliseconds 750
    if (-not $folderTarget.Current.HasKeyboardFocus) { throw 'Script folder settings target did not receive focus.' }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 600
    $null = Find-Control -Root $window -AutomationId 'HelpAboutPage'

    Invoke-Control (Find-Control -Root $window -AutomationId 'HelpInterpreterButton')
    $interpreterTarget = Find-Control -Root $window -AutomationId 'SettingsAddInterpreterButton'
    Start-Sleep -Milliseconds 750
    if (-not $interpreterTarget.Current.HasKeyboardFocus) { throw 'Interpreter settings target did not receive focus.' }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 600

    Invoke-Control (Find-Control -Root $window -AutomationId 'HelpRunGuideButton')
    $null = Find-Control -Root $window -AutomationId 'HelpLocalTopicTitle'
    Invoke-Control (Find-Control -Root $window -AutomationId 'HelpFaqButton')
    $copyButton = Find-Control -Root $window -AutomationId 'HelpCopyInfoButton'
    Invoke-Control $copyButton
    Start-Sleep -Milliseconds 500
    Invoke-Control (Find-Control -Root $window -AutomationId 'HelpCheckUpdatesButton')
    $updateStatus = Find-Control -Root $window -AutomationId 'HelpUpdateStatusText' -TimeoutSeconds 15
    $updateDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $updateStatusText = $updateStatus.Current.Name
        if ($updateStatusText -notmatch '正在检查|Checking') { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $updateDeadline)
    if ($updateStatusText -match '正在检查|Checking') { throw 'Update check did not complete.' }
    if ($updateStatusText -notmatch '尚未发布正式稳定版|No published stable release') {
        throw "Unexpected update status: $updateStatusText"
    }
    Save-WindowScreenshot -Window $window -Path $lightScreenshot

    Invoke-Control (Find-Control -Root $window -AutomationId 'TitleBarThemeButton')
    Start-Sleep -Milliseconds 700
    $null = Find-Control -Root $window -AutomationId 'HelpAboutPage'
    Save-WindowScreenshot -Window $window -Path $darkScreenshot

    [HelpUiNative]::MoveWindow([IntPtr]$window.Current.NativeWindowHandle, 100, 80, 1050, 900, $true) | Out-Null
    Start-Sleep -Milliseconds 700
    if ($process.HasExited) { throw 'PyRunner exited while applying the narrow layout.' }
    Save-WindowScreenshot -Window $window -Path $narrowScreenshot

    [pscustomobject]@{
        HelpPageVisibleAfterThemeSwitch = $true
        SettingsSectionFocus = $true
        LocalHelpTopics = $true
        CopyActionInvoked = $true
        UpdateStatusVisible = $true
        UpdateStatusText = $updateStatusText
        LightScreenshot = $lightScreenshot
        DarkScreenshot = $darkScreenshot
        NarrowScreenshot = $narrowScreenshot
    } | ConvertTo-Json
}
finally {
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) { $process.Kill() }
    }
    if ($dataDirectory.StartsWith([System.IO.Path]::GetTempPath(), [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
