param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PointerInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@

$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsDirectory = Join-Path $solutionRoot 'artifacts'
$dataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "PyRunner-ButtonFocus-$PID-$(Get-Date -Format 'yyyyMMddHHmmssfff')"
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
@{ FirstRunCompleted = $true; FirstRunVersion = 1; Theme = 'Dark' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataDirectory 'settings.json') -Encoding utf8
$executable = Join-Path $solutionRoot "PyRunner\bin\x64\$Configuration\net8.0-windows10.0.19041.0\PyRunner.exe"
$previousDataDirectory = [Environment]::GetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', 'Process')
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $dataDirectory, 'Process')
$process = Start-Process -FilePath $executable -PassThru
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $previousDataDirectory, 'Process')

try {
    $window = $null
    for ($attempt = 0; $attempt -lt 40 -and $null -eq $window; $attempt++) {
        Start-Sleep -Milliseconds 250
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
    }
    if ($null -eq $window) { throw 'PyRunner window was not found.' }

    $themeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'TitleBarThemeButton')
    $themeButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $themeCondition)
    if ($null -eq $themeButton) { throw 'Theme button was not found.' }

    [PointerInput]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 250
    $buttonBounds = $themeButton.Current.BoundingRectangle
    [PointerInput]::SetCursorPos(
        [int]($buttonBounds.Left + $buttonBounds.Width / 2),
        [int]($buttonBounds.Top + $buttonBounds.Height / 2)) | Out-Null
    [PointerInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [PointerInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    [PointerInput]::SetCursorPos(10, 10) | Out-Null
    Start-Sleep -Milliseconds 500

    $bounds = $window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$bounds.Left, [int]$bounds.Top, 0, 0, $bitmap.Size)
        $output = Join-Path $artifactsDirectory "button-focus-$($Configuration.ToLowerInvariant())-$(Get-Date -Format 'yyyyMMddHHmmssfff').png"
        $bitmap.Save($output, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output $output
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
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
