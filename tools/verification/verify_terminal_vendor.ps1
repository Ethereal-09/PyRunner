param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TerminalVendorNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@

$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$dataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "PyRunner-TerminalVendor-$PID-$(Get-Date -Format 'yyyyMMddHHmmssfff')"
$scriptDirectory = Join-Path $dataDirectory 'scripts'
New-Item -ItemType Directory -Path $scriptDirectory -Force | Out-Null
$probeScript = @'
import os
import sys
import time

print("\x1b[32mANSI_OK\x1b[0m", flush=True)
print("UNICODE_OK: 中文 English", flush=True)
shortcut = input("PASTE_SHORTCUT>")
print("SHORTCUT_ECHO:" + shortcut, flush=True)
right_click = input("PASTE_RIGHTCLICK>")
print("RIGHTCLICK_ECHO:" + right_click, flush=True)
keyboard = input("KEYBOARD>")
print("KEYBOARD_ECHO:" + keyboard, flush=True)
size = os.get_terminal_size()
print(f"PTY_SIZE:{size.columns}x{size.lines}", flush=True)
print("CTRL_C_READY", flush=True)
try:
    while True:
        time.sleep(0.1)
except KeyboardInterrupt:
    print("CTRL_C_OK", flush=True)
    sys.exit(0)
'@
Set-Content -LiteralPath (Join-Path $scriptDirectory 'vendor_terminal_probe.py') -Value $probeScript -Encoding utf8NoBOM

$executable = (Resolve-Path -LiteralPath (Join-Path $solutionRoot "PyRunner\bin\x64\$Configuration\net8.0-windows10.0.19041.0\PyRunner.exe")).Path
$debugPort = Get-Random -Minimum 9300 -Maximum 9800
$previousDataDirectory = [Environment]::GetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', 'Process')
$previousBrowserArguments = [Environment]::GetEnvironmentVariable('WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS', 'Process')
$previousClipboard = Get-Clipboard -Raw -ErrorAction SilentlyContinue
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $dataDirectory, 'Process')
$process = Start-Process -FilePath $executable -PassThru
[Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $previousDataDirectory, 'Process')

$socket = $null
$cdpId = 0
$completed = $false

function Get-ProcessWindows {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Wait-ForWindowContaining {
    param([string]$AutomationId, [int]$TimeoutSeconds = 25)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($candidate in $candidates) {
            $candidateProcessId = $candidate.Current.ProcessId
            if ($null -eq $candidateProcessId -or $candidateProcessId -le 0) { continue }
            $candidateProcess = Get-Process -Id $candidateProcessId -ErrorAction SilentlyContinue
            if ($null -eq $candidateProcess -or $candidateProcess.ProcessName -ne 'PyRunner') { continue }
            if ($null -ne $candidate.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
                return $candidate
            }
        }
        Start-Sleep -Milliseconds 200
    }
    $diagnostics = @()
    $remainingWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($candidate in $remainingWindows) {
        $candidateProcessId = $candidate.Current.ProcessId
        if ($null -eq $candidateProcessId -or $candidateProcessId -le 0) { continue }
        $candidateProcess = Get-Process -Id $candidateProcessId -ErrorAction SilentlyContinue
        if ($null -eq $candidateProcess -or $candidateProcess.ProcessName -ne 'PyRunner') { continue }
        $ids = $candidate.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.AutomationId } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique -First 20
        $diagnostics += "window='$($candidate.Current.Name)' ids='$($ids -join ',')'"
    }
    throw "Timed out waiting for a window containing '$AutomationId'. $($diagnostics -join '; ')"
}

function Find-Control {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutSeconds = 12
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
        [int]$TimeoutSeconds = 15
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
    throw "Control named '$Name' was not found."
}

function Invoke-Control {
    param([System.Windows.Automation.AutomationElement]$Control)
    $pattern = $Control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 500
}

function Click-Control {
    param(
        [System.Windows.Automation.AutomationElement]$Control,
        [ValidateSet('Left', 'Right')][string]$Button = 'Left',
        [double]$VerticalRatio = 0.5
    )
    $bounds = $Control.Current.BoundingRectangle
    $x = [int]($bounds.Left + [Math]::Min([Math]::Max($bounds.Width * 0.5, 8), $bounds.Width - 8))
    $y = [int]($bounds.Top + [Math]::Min([Math]::Max($bounds.Height * $VerticalRatio, 8), $bounds.Height - 8))
    [TerminalVendorNative]::SetCursorPos($x, $y) | Out-Null
    if ($Button -eq 'Right') {
        [TerminalVendorNative]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
        [TerminalVendorNative]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    }
    else {
        [TerminalVendorNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [TerminalVendorNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 350
}

function Invoke-Cdp {
    param([string]$Method, [hashtable]$Parameters = @{})
    $script:cdpId++
    $requestId = $script:cdpId
    $request = @{ id = $requestId; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 12 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($request)
    $segment = [ArraySegment[byte]]::new($bytes)
    $null = $script:socket.SendAsync(
        $segment,
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()

    while ($true) {
        $stream = [System.IO.MemoryStream]::new()
        try {
            do {
                $buffer = New-Object byte[] 65536
                $receiveSegment = [ArraySegment[byte]]::new($buffer)
                $result = $script:socket.ReceiveAsync(
                    $receiveSegment,
                    [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
                if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                    throw 'The WebView2 DevTools socket closed unexpectedly.'
                }
                $stream.Write($buffer, 0, $result.Count)
            } while (-not $result.EndOfMessage)
            $message = [System.Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json
            if ($message.id -eq $requestId) {
                if ($null -ne $message.error) { throw ($message.error | ConvertTo-Json -Compress) }
                return $message.result
            }
        }
        finally { $stream.Dispose() }
    }
}

function Evaluate-JavaScript {
    param([string]$Expression)
    $result = Invoke-Cdp -Method 'Runtime.evaluate' -Parameters @{
        expression = $Expression
        returnByValue = $true
        awaitPromise = $true
    }
    if ($null -ne $result.exceptionDetails) {
        throw ($result.exceptionDetails | ConvertTo-Json -Depth 8 -Compress)
    }
    return $result.result.value
}

function Send-CdpEnter {
    Invoke-Cdp -Method 'Input.dispatchKeyEvent' -Parameters @{
        type = 'keyDown'
        key = 'Enter'
        code = 'Enter'
        text = "`r"
        unmodifiedText = "`r"
        windowsVirtualKeyCode = 13
        nativeVirtualKeyCode = 13
    } | Out-Null
    Invoke-Cdp -Method 'Input.dispatchKeyEvent' -Parameters @{
        type = 'keyUp'
        key = 'Enter'
        code = 'Enter'
        windowsVirtualKeyCode = 13
        nativeVirtualKeyCode = 13
    } | Out-Null
}

function Send-CdpText {
    param([string]$Text)
    foreach ($character in $Text.ToCharArray()) {
        $key = [string]$character
        $virtualKey = [int][char]([string]$character).ToUpperInvariant()[0]
        Invoke-Cdp -Method 'Input.dispatchKeyEvent' -Parameters @{
            type = 'keyDown'
            key = $key
            text = $key
            unmodifiedText = $key
            windowsVirtualKeyCode = $virtualKey
            nativeVirtualKeyCode = $virtualKey
        } | Out-Null
        Invoke-Cdp -Method 'Input.dispatchKeyEvent' -Parameters @{
            type = 'keyUp'
            key = $key
            windowsVirtualKeyCode = $virtualKey
            nativeVirtualKeyCode = $virtualKey
        } | Out-Null
    }
}

function Send-CdpCtrlC {
    Invoke-Cdp -Method 'Input.dispatchKeyEvent' -Parameters @{
        type = 'rawKeyDown'
        modifiers = 2
        key = 'c'
        code = 'KeyC'
        windowsVirtualKeyCode = 67
        nativeVirtualKeyCode = 67
    } | Out-Null
    Invoke-Cdp -Method 'Input.dispatchKeyEvent' -Parameters @{
        type = 'keyUp'
        modifiers = 2
        key = 'c'
        code = 'KeyC'
        windowsVirtualKeyCode = 67
        nativeVirtualKeyCode = 67
    } | Out-Null
}

function Wait-ForTerminalText {
    param([string]$Expected, [int]$TimeoutSeconds = 12)
    $expression = "Array.from({length:term.buffer.active.length},(_,i)=>term.buffer.active.getLine(i)?.translateToString(true)||'').join('\\n')"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $text = [string](Evaluate-JavaScript $expression)
        if ($text.Contains($Expected, [StringComparison]::Ordinal)) { return $text }
        Start-Sleep -Milliseconds 200
    }
    $tail = if ($text.Length -gt 1200) { $text.Substring($text.Length - 1200) } else { $text }
    throw "Terminal output did not contain '$Expected'. Buffer tail: $tail"
}

function Wait-ForTerminalTarget {
    param([int]$TimeoutSeconds = 20)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$debugPort/json" -TimeoutSec 2
            $target = $targets | Where-Object { $_.type -eq 'page' -and $_.url -match 'index\.html' } | Select-Object -First 1
            if ($null -ne $target) { return $target }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    }
    throw 'The terminal WebView2 DevTools target was not found.'
}

$results = [ordered]@{}
$mainWindow = $null
try {
    $onboardingWindow = Wait-ForWindowContaining -AutomationId 'OnboardingNextButton'
    Invoke-Control (Find-Control -Root $onboardingWindow -AutomationId 'OnboardingNextButton')
    $pathInput = Find-Control -Root $onboardingWindow -AutomationId 'OnboardingPathInput'
    $pathInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($scriptDirectory)
    Invoke-Control (Find-Control -Root $onboardingWindow -AutomationId 'OnboardingAddPathButton')
    Invoke-Control (Find-Control -Root $onboardingWindow -AutomationId 'OnboardingNextButton')
    Start-Sleep -Seconds 3
    Invoke-Control (Find-Control -Root $onboardingWindow -AutomationId 'OnboardingNextButton')
    Invoke-Control (Find-Control -Root $onboardingWindow -AutomationId 'OnboardingCenterStartButton')

    $settingsPath = Join-Path $dataDirectory 'settings.json'
    $settingsDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $settings = if (Test-Path -LiteralPath $settingsPath) {
            Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
        }
        if ($null -ne $settings -and $settings.FirstRunCompleted) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $settingsDeadline)
    if ($null -eq $settings -or -not $settings.FirstRunCompleted) {
        throw 'Onboarding did not persist completion before the terminal regression run.'
    }
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $dataDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS', "--remote-debugging-port=$debugPort", 'Process')
    $process = Start-Process -FilePath $executable -PassThru
    [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY', $previousDataDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS', $previousBrowserArguments, 'Process')

    $mainWindow = Wait-ForWindowContaining -AutomationId 'RunButton'
    $process = Get-Process -Id $mainWindow.Current.ProcessId
    [TerminalVendorNative]::SetForegroundWindow([IntPtr]$mainWindow.Current.NativeWindowHandle) | Out-Null
    $scriptItem = Find-ControlByName -Root $mainWindow -Name 'vendor_terminal_probe.py'
    Click-Control -Control $scriptItem
    $workspace = Find-Control -Root $mainWindow -AutomationId 'WorkspaceTabs'
    Invoke-Control (Find-Control -Root $mainWindow -AutomationId 'RunButton')

    $target = Wait-ForTerminalTarget
    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $null = $socket.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Invoke-Cdp -Method 'Page.enable' | Out-Null
    Invoke-Cdp -Method 'Runtime.enable' | Out-Null
    Evaluate-JavaScript "window.__pyrunnerErrors=[];addEventListener('error',e=>window.__pyrunnerErrors.push(String(e.message||e.error)));addEventListener('unhandledrejection',e=>window.__pyrunnerErrors.push(String(e.reason)));true" | Out-Null

    $initialState = (Evaluate-JavaScript "JSON.stringify({terminal:typeof Terminal,fit:typeof FitAddon?.FitAddon,cols:term?.cols,rows:term?.rows,errors:window.__pyrunnerErrors||[]})") | ConvertFrom-Json
    if ($initialState.terminal -ne 'function' -or $initialState.fit -ne 'function' -or $initialState.cols -le 1 -or $initialState.rows -le 0) {
        throw "xterm/FitAddon did not initialize: $($initialState | ConvertTo-Json -Compress)"
    }
    if ($initialState.errors.Count -ne 0) { throw "WebView2 JavaScript errors: $($initialState.errors -join '; ')" }
    $results.XtermInitialized = $true
    $results.FitAddonLoaded = $true
    $results.InitialSize = "$($initialState.cols)x$($initialState.rows)"

    $null = Wait-ForTerminalText -Expected 'PASTE_SHORTCUT>'
    Evaluate-JavaScript 'term.focus(); true' | Out-Null
    Click-Control -Control $workspace -VerticalRatio 0.65
    Set-Clipboard -Value '快捷粘贴-Clipboard'
    [System.Windows.Forms.SendKeys]::SendWait('^+v')
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $null = Wait-ForTerminalText -Expected 'SHORTCUT_ECHO:快捷粘贴-Clipboard'

    Set-Clipboard -Value '右键粘贴-ContextMenu'
    Click-Control -Control $workspace -Button Right -VerticalRatio 0.65
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $null = Wait-ForTerminalText -Expected 'RIGHTCLICK_ECHO:右键粘贴-ContextMenu'

    $beforeResize = (Evaluate-JavaScript "JSON.stringify({cols:term.cols,rows:term.rows,theme:document.documentElement.dataset.theme})") | ConvertFrom-Json
    [TerminalVendorNative]::MoveWindow([IntPtr]$mainWindow.Current.NativeWindowHandle, 80, 70, 1180, 760, $true) | Out-Null
    Start-Sleep -Seconds 1
    $afterResize = (Evaluate-JavaScript "JSON.stringify({cols:term.cols,rows:term.rows,theme:document.documentElement.dataset.theme})") | ConvertFrom-Json
    if ($afterResize.cols -le 1 -or $afterResize.rows -le 0 -or
        ($afterResize.cols -eq $beforeResize.cols -and $afterResize.rows -eq $beforeResize.rows)) {
        throw "FitAddon did not update terminal dimensions: before=$($beforeResize.cols)x$($beforeResize.rows), after=$($afterResize.cols)x$($afterResize.rows)"
    }
    $results.ResizeUpdated = $true
    $results.ResizedTo = "$($afterResize.cols)x$($afterResize.rows)"

    Evaluate-JavaScript 'term.focus(); true' | Out-Null
    Click-Control -Control $workspace -VerticalRatio 0.65
    Send-CdpText -Text 'Keyboard42'
    Start-Sleep -Milliseconds 200
    Send-CdpEnter
    $terminalText = Wait-ForTerminalText -Expected 'CTRL_C_READY'
    if (-not $terminalText.Contains("KEYBOARD_ECHO:Keyboard42", [StringComparison]::Ordinal)) {
        throw "Keyboard input was not echoed by Python. Buffer: $terminalText"
    }
    $sizeMatch = [regex]::Match($terminalText, 'PTY_SIZE:(\d+)x(\d+)')
    if (-not $sizeMatch.Success) { throw 'Python did not report its ConPTY size.' }
    $ptyColumns = [int]$sizeMatch.Groups[1].Value
    $ptyRows = [int]$sizeMatch.Groups[2].Value
    if ($ptyColumns -ne [int]$afterResize.cols -or $ptyRows -ne [int]$afterResize.rows) {
        throw "ConPTY size did not match xterm: pty=${ptyColumns}x${ptyRows}, xterm=$($afterResize.cols)x$($afterResize.rows)"
    }
    $results.ClipboardShortcut = $true
    $results.RightClickPaste = $true
    $results.KeyboardInput = $true
    $results.UnicodeAndAnsi = $terminalText.Contains('ANSI_OK', [StringComparison]::Ordinal) -and
        $terminalText.Contains('UNICODE_OK:', [StringComparison]::Ordinal) -and
        $terminalText.Contains('中文', [StringComparison]::Ordinal) -and
        $terminalText.Contains('English', [StringComparison]::Ordinal)
    $results.ConPtySizeSynchronized = $true

    $themeButton = Find-Control -Root $mainWindow -AutomationId 'TitleBarThemeButton'
    Invoke-Control $themeButton
    $themeDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $themeState = (Evaluate-JavaScript "JSON.stringify({name:document.documentElement.dataset.theme,bg:term.options.theme.background})") | ConvertFrom-Json
        if ($themeState.name -ne $afterResize.theme) { break }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $themeDeadline)
    if ($themeState.name -eq $afterResize.theme) { throw 'Terminal theme did not update after the app theme changed.' }
    Invoke-Control $themeButton
    $restoredTheme = (Evaluate-JavaScript 'document.documentElement.dataset.theme')
    if ($restoredTheme -ne $afterResize.theme) { throw 'Terminal theme did not restore after the second theme toggle.' }
    $results.LightAndDarkThemes = $true

    Evaluate-JavaScript 'term.focus(); true' | Out-Null
    Send-CdpCtrlC
    $finalText = Wait-ForTerminalText -Expected 'CTRL_C_OK'
    $results.CtrlC = $finalText.Contains('CTRL_C_OK', [StringComparison]::Ordinal)
    $finalErrors = @(Evaluate-JavaScript 'window.__pyrunnerErrors||[]')
    if ($finalErrors.Count -ne 0) { throw "WebView2 JavaScript errors: $($finalErrors -join '; ')" }
    $results.WebView2JavaScriptErrors = 0
    $results.Verified = $results.XtermInitialized -and
        $results.FitAddonLoaded -and
        $results.ResizeUpdated -and
        $results.ClipboardShortcut -and
        $results.RightClickPaste -and
        $results.KeyboardInput -and
        $results.UnicodeAndAnsi -and
        $results.ConPtySizeSynchronized -and
        $results.LightAndDarkThemes -and
        $results.CtrlC -and
        $results.WebView2JavaScriptErrors -eq 0
    if (-not $results.Verified) { throw 'One or more terminal vendor regression checks failed.' }
    $completed = $true
    $results | ConvertTo-Json -Depth 4
}
finally {
    if ($null -ne $socket) {
        try { $socket.Dispose() } catch { }
    }
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    if ($null -ne $previousClipboard) {
        Set-Clipboard -Value $previousClipboard
    }
    if ($completed -and $dataDirectory.StartsWith([System.IO.Path]::GetTempPath(), [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    elseif (-not $completed) {
        Write-Warning "Terminal vendor test data preserved for diagnosis: $dataDirectory"
    }
}
