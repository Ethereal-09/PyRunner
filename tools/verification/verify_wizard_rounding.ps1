param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WizardWindowActivation {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
}
'@

$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsDirectory = Join-Path $solutionRoot 'artifacts'
$dataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "PyRunner-WizardRounding-$PID-$(Get-Date -Format 'yyyyMMddHHmmssfff')"
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$settingsPath = Join-Path $dataDirectory 'settings.json'
@{ FirstRunCompleted = $false; FirstRunVersion = 0; Theme = 'Dark' } |
    ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8

$executable = Join-Path $solutionRoot "PyRunner\bin\x64\$Configuration\net8.0-windows10.0.19041.0\PyRunner.exe"
$previousDataDirectory = [Environment]::GetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', 'Process')
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $dataDirectory, 'Process')
$process = Start-Process -FilePath $executable -PassThru
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $previousDataDirectory, 'Process')

try {
    $window = $null
    for ($attempt = 0; $attempt -lt 40 -and $null -eq $window; $attempt++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if (-not $process.HasExited -and $process.MainWindowHandle -ne 0) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle(
                [IntPtr]$process.MainWindowHandle)
        }
    }
    if ($null -eq $window) { throw 'PyRunner window was not found.' }

    [WizardWindowActivation]::SetForegroundWindow([IntPtr]$process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 1000
    $bounds = $window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $deviceContext = $graphics.GetHdc()
        try {
            if (-not [WizardWindowActivation]::PrintWindow(
                [IntPtr]$process.MainWindowHandle,
                $deviceContext,
                2)) {
                throw 'PrintWindow failed.'
            }
        }
        finally {
            $graphics.ReleaseHdc($deviceContext)
        }
        $output = Join-Path $artifactsDirectory 'wizard-rounded-final.png'
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
