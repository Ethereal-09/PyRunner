param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [string]$ExecutablePath, [switch]$VerifyExits)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ReviewNative {
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int z,bool repaint);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,IntPtr info);
}
'@
$solutionRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$outputDirectory = Join-Path $solutionRoot ('artifacts\review-ui-' + [Guid]::NewGuid().ToString('N'))
$fixtureDirectory = Join-Path $outputDirectory 'data'
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
Push-Location $solutionRoot
try {
 & dotnet run --project .\PyRunner.Verification\PyRunner.Verification.csproj -c Debug --no-build -- --prepare-review-ui $fixtureDirectory
 if ($LASTEXITCODE -ne 0) { throw 'UI fixture preparation failed.' }
} finally { Pop-Location }
function Find-Control($id) {
 $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 for ($i=0;$i -lt 40;$i++) {
  if ($process -and $process.HasExited) { throw "Application exited: $($process.ExitCode), looking for $id" }
  $element = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
  if(!$element) {$element = Find-InTestProcess $condition}
  if ($element) { return $element }
  Start-Sleep -Milliseconds 150
 }
 throw "Control missing: $id"
}
function Click($element) { $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500 }
function Find-InTestProcess($condition) {
 $processCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
 foreach($topLevel in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$processCondition)) {
  $found = $topLevel.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
  if($found) {return $found}
 }
 return $null
}
function Read-RunNotice {
 $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'RunResultNotice')
 $notice = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
 if(!$notice -or $notice.Current.IsOffscreen) {return ''}
 $names = @($notice.Current.Name)
 foreach($child in $notice.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)) { $names += $child.Current.Name }
 return $names -join ' '
}
function Wait-RunStatus($expected) {
 for($i=0;$i -lt 80;$i++) {
  if((Find-Control 'SelectedScriptStatus').Current.Name -match $expected) {return}
  Start-Sleep -Milliseconds 150
 }
 throw "Run did not finish with status: $expected"
}
function Ensure-Foreground {
 if([ReviewNative]::GetForegroundWindow() -eq $handle) {return}
 [ReviewNative]::ShowWindow($handle,9) | Out-Null
 try {
  $focusCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'SidebarSearchBox')
  $focusControl = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$focusCondition)
  if($focusControl) {$focusControl.SetFocus()}
 } catch { }
 for($i=0;$i -lt 5;$i++) {
  [ReviewNative]::SetForegroundWindow($handle) | Out-Null
  Start-Sleep -Milliseconds 100
  if([ReviewNative]::GetForegroundWindow() -eq $handle) {return}
 }
 throw 'Test window is not foreground; pointer input was blocked to avoid acting on another application.'
}
function Assert-FeatureAbsent {
 foreach($id in @('NavAiButton','DependencyExpander','AiAssistantPageHost','AiGroupTitle')) {
  $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
  if($script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)) {throw "Removed feature is still exposed: $id"}
 }
}
function Click-Named($name) {
 $condition = [Windows.Automation.AndCondition]::new(
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name),
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button))
 $element = $null
 for($i=0;$i -lt 20 -and !$element;$i++) {
  $element = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
  if(!$element) {
   $element = Find-InTestProcess $condition
  }
  if(!$element) {Start-Sleep -Milliseconds 100}
 }
 if (!$element) { throw "Button missing: $name" }
 Click $element
}
function Screenshot($name) {
 Ensure-Foreground
 $bounds = $script:window.Current.BoundingRectangle
 $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width,[int]$bounds.Height)
 $graphics = [Drawing.Graphics]::FromImage($bitmap)
 try { $graphics.CopyFromScreen([int]$bounds.Left,[int]$bounds.Top,0,0,$bitmap.Size); $bitmap.Save((Join-Path $outputDirectory "$name.png"),[Drawing.Imaging.ImageFormat]::Png) }
 finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$oldDirectory = [Environment]::GetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY','Process')
try {
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$fixtureDirectory,'Process')
 $exe = Join-Path $solutionRoot "PyRunner\bin\x64\$Configuration\net8.0-windows10.0.19041.0\PyRunner.exe"
 if($ExecutablePath) { $exe = [IO.Path]::GetFullPath($ExecutablePath) }
 $process = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$oldDirectory,'Process')
 $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
 for($i=0;$i -lt 80;$i++) {
  $script:window = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
  if($script:window) { break }; Start-Sleep -Milliseconds 150
 }
 if(!$script:window) { throw 'Window missing.' }
 $handle = [IntPtr]$script:window.Current.NativeWindowHandle
 [ReviewNative]::SetForegroundWindow($handle) | Out-Null
 [ReviewNative]::MoveWindow($handle,20,20,2400,1300,$true) | Out-Null
 Assert-FeatureAbsent
 Start-Sleep -Milliseconds 800
 Screenshot 'initial'
 Click (Find-Control 'TitleBarSettingsButton')
 $checkbox = Find-Control 'SettingsOptNotifyOnFail'
 $initialState = $checkbox.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
 $checkbox.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
 Screenshot 'settings'
 Assert-FeatureAbsent
 Click-Named '取消'
 Click (Find-Control 'TitleBarSettingsButton')
 $checkbox = Find-Control 'SettingsOptNotifyOnFail'
 if($checkbox.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne $initialState) { throw 'Cancel did not discard the setting.' }
 Write-Output 'PASS: settings Cancel discards run options'
 $checkbox.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
 Click-Named '确定'
 Click (Find-Control 'TitleBarSettingsButton')
 $checkbox = Find-Control 'SettingsOptNotifyOnFail'
 if($checkbox.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq $initialState) { throw 'OK did not apply the setting.' }
 Write-Output 'PASS: settings OK applies run options'
 Click-Named '取消'
 Click (Find-Control 'NavRunsButton'); Screenshot 'history-wide'
 [ReviewNative]::MoveWindow($handle,80,80,1050,900,$true) | Out-Null
 Start-Sleep -Milliseconds 700; Screenshot 'history-narrow'
 [ReviewNative]::MoveWindow($handle,20,20,2400,1300,$true) | Out-Null
 Start-Sleep -Milliseconds 600
 Click (Find-Control 'NavSchedulesButton')
 [ReviewNative]::MoveWindow($handle,80,80,1050,900,$true) | Out-Null
 Start-Sleep -Milliseconds 600; Screenshot 'schedules-narrow'
 [ReviewNative]::MoveWindow($handle,20,20,2400,1300,$true) | Out-Null
 Start-Sleep -Milliseconds 600
 Screenshot 'before-run'
 # WinUI 重建标题栏后旧 UIA peer 可能不再暴露导航；用真实指针检查用户入口。
 Ensure-Foreground
 $bounds = $script:window.Current.BoundingRectangle
 $scale = [ReviewNative]::GetDpiForWindow($handle) / 96.0
 [ReviewNative]::SetCursorPos([int]($bounds.Left + 208*$scale),[int]($bounds.Top + 30*$scale)) | Out-Null
 [ReviewNative]::mouse_event(2,0,0,0,[IntPtr]::Zero)
 [ReviewNative]::mouse_event(4,0,0,0,[IntPtr]::Zero)
 Start-Sleep -Milliseconds 700
 $script:window = [Windows.Automation.AutomationElement]::FromHandle($handle)
 $codeButton = Find-Control 'CodeModeButton'
 Ensure-Foreground
 $codeBounds = $codeButton.Current.BoundingRectangle
 [ReviewNative]::SetCursorPos([int]($codeBounds.Left + $codeBounds.Width/2),[int]($codeBounds.Top + $codeBounds.Height/2)) | Out-Null
 [ReviewNative]::mouse_event(2,0,0,0,[IntPtr]::Zero)
 [ReviewNative]::mouse_event(4,0,0,0,[IntPtr]::Zero)
 Start-Sleep -Seconds 3
 Screenshot 'offline-editor'
 $terminalButton = Find-Control 'TerminalModeButton'
 Ensure-Foreground
 $terminalBounds = $terminalButton.Current.BoundingRectangle
 [ReviewNative]::SetCursorPos([int]($terminalBounds.Left + $terminalBounds.Width/2),[int]($terminalBounds.Top + $terminalBounds.Height/2)) | Out-Null
 [ReviewNative]::mouse_event(2,0,0,0,[IntPtr]::Zero)
 [ReviewNative]::mouse_event(4,0,0,0,[IntPtr]::Zero)
 Start-Sleep -Milliseconds 600
 Screenshot 'scripts-core'
 Assert-FeatureAbsent
 Click (Find-Control 'RunButton')
 Start-Sleep -Seconds 2
 if ((Find-Control 'SelectedScriptStatus').Current.Name -match '运行中') {
  [ReviewNative]::PostMessage($handle,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
  Start-Sleep -Milliseconds 600
  Screenshot 'close-running-confirmation'
  Click-Named '取消'
  if ($process.HasExited) { throw 'Cancel closed the running application.' }
  Write-Output 'PASS: closing a running application requires confirmation and Cancel preserves it'
  Click (Find-Control 'StopButton')
  Start-Sleep -Seconds 3
 } else { throw 'Review script did not enter running state.' }
 if($VerifyExits) {
  if(Read-RunNotice) {throw 'Stopping a script displayed a failure notice.'}
  Write-Output 'PASS: user stop does not display an error notice'
  Click (Find-Control 'TitleBarSettingsButton')
  $checkbox = Find-Control 'SettingsOptNotifyOnFail'
  $toggle = $checkbox.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
  if($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) {$toggle.Toggle()}
  Click-Named '确定'
  $scriptFile = Join-Path $fixtureDirectory 'scripts\review_sample.py'
  [IO.File]::WriteAllText($scriptFile,"print('EXIT_FIX_CODE_2', flush=True)`nraise SystemExit(2)`n")
  Click (Find-Control 'RunButton')
  Wait-RunStatus '失败'
  Start-Sleep -Milliseconds 500
  $notice = Read-RunNotice
  if($notice -notmatch '退出码为 2') {throw "Exit code 2 notice is missing: $notice"}
  # A blocking dialog would prevent this settings action from opening.
  Click (Find-Control 'TitleBarSettingsButton')
  Find-Control 'SettingsOptNotifyOnFail' | Out-Null
  Click-Named '取消'
  Screenshot 'exit-code-2-notice'
  Write-Output 'PASS: nonzero exit shows the real code without blocking other actions'
  [IO.File]::WriteAllText($scriptFile,"print('EXIT_FIX_SUCCESS', flush=True)`n")
  Click (Find-Control 'RunButton')
  Wait-RunStatus '成功'
  if(Read-RunNotice) {throw 'A successful run retained a failure notice.'}
  Write-Output 'PASS: normal exit does not display an error notice'
  Click (Find-Control 'TitleBarSettingsButton')
  $checkbox = Find-Control 'SettingsOptNotifyOnFail'
  $toggle = $checkbox.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
  if($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {$toggle.Toggle()}
  Click-Named '确定'
  [IO.File]::WriteAllText($scriptFile,"print('EXIT_FIX_NOTIFY_OFF', flush=True)`nraise SystemExit(2)`n")
  Click (Find-Control 'RunButton')
  Wait-RunStatus '失败'
  if(Read-RunNotice) {throw 'Disabled failure notifications still displayed a notice.'}
  Write-Output 'PASS: disabling failure notifications suppresses the notice'
  Push-Location $solutionRoot
  try {
   & dotnet run --project .\PyRunner.Verification\PyRunner.Verification.csproj -c Debug --no-build -- --verify-review-exits $fixtureDirectory
   if($LASTEXITCODE -ne 0) {throw 'Exit history validation failed.'}
  } finally {Pop-Location}
 }
 Write-Output "Screenshots: $outputDirectory"
} finally {
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$oldDirectory,'Process')
 if($process -and !$process.HasExited) { $process.CloseMainWindow() | Out-Null; if(!$process.WaitForExit(5000)) { $process.Kill() } }
}
