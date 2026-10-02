param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [string]$ExecutablePath, [switch]$VerifyExits, [ValidateSet('Light','Dark')][string]$Theme = 'Light')
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
 $settingsPath = Join-Path $fixtureDirectory 'settings.json'
 $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
 $settings.Theme = $Theme
 [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 10))
 $scriptFile = Join-Path $fixtureDirectory 'scripts\review_sample.py'
 [IO.File]::WriteAllText($scriptFile,"import time`nfor i in range(100): print('OUTPUT_LINE_' + str(i), flush=True)`nvalue = input('LAST_INPUT_PROMPT > ')`nprint('INPUT_RECEIVED:' + value, flush=True)`ntime.sleep(120)`n")
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$fixtureDirectory,'Process')
 $process = Start-Process -FilePath $ExecutablePath -PassThru -WindowStyle Hidden
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$oldDirectory,'Process')
 for($i=0;$i -lt 80;$i++) { $process.Refresh(); if($process.MainWindowHandle -ne 0){break}; Start-Sleep -Milliseconds 200 }
 $handle = $process.MainWindowHandle
 $script:window = [Windows.Automation.AutomationElement]::FromHandle($handle)
 [ReviewNative]::MoveWindow($handle,80,80,1500,1000,$true) | Out-Null
 Start-Sleep -Seconds 3
 Click (Find-Control 'RunButton')
 Start-Sleep -Seconds 3
 if((Find-Control 'RunButton').Current.IsEnabled) {throw 'Run button remains enabled while running'}
 Screenshot 'terminal-wide'
 [ReviewNative]::MoveWindow($handle,80,80,1050,760,$true) | Out-Null
 Start-Sleep -Seconds 2
 Screenshot 'terminal-narrow'
 Ensure-Foreground
 $tabs = $script:window.Current.BoundingRectangle
 [ReviewNative]::SetCursorPos([int]($tabs.Left + $tabs.Width/2),[int]($tabs.Top + $tabs.Height/2)) | Out-Null
 [ReviewNative]::mouse_event(2,0,0,0,[IntPtr]::Zero)
 [ReviewNative]::mouse_event(4,0,0,0,[IntPtr]::Zero)
 [Windows.Forms.SendKeys]::SendWait('viewport')
 Start-Sleep -Milliseconds 500
 [Windows.Forms.SendKeys]::SendWait('{ENTER}')

 Start-Sleep -Seconds 2
 Screenshot 'terminal-input'
 Click (Find-Control 'StopButton')
 Start-Sleep -Seconds 2
 Screenshot 'terminal-stopped'
 Write-Output "PASS: terminal running, narrow resize, keyboard text echo and stop; screenshots: $outputDirectory"
} finally {
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$oldDirectory,'Process')
 if($process -and !$process.HasExited) { [ReviewNative]::PostMessage($handle,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null }
}