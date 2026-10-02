param([string]$ExecutablePath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DeleteTestNative {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,IntPtr info);
}
'@
$solutionRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output=Join-Path $solutionRoot ('artifacts\tree-delete-test-'+[Guid]::NewGuid().ToString('N'))
$data=Join-Path $output 'data'
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location $solutionRoot
$exe=[IO.Path]::GetFullPath($ExecutablePath)
$oldData=[Environment]::GetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY','Process')
function Find($id) {
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 for($i=0;$i -lt 30;$i++) {
  $element=$script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
  if($element){return $element}
  Start-Sleep -Milliseconds 100
 }
 throw "Missing control: $id"
}
function Foreground {
 if([DeleteTestNative]::GetForegroundWindow() -eq $script:handle){return}
 [DeleteTestNative]::ShowWindow($script:handle,9) | Out-Null
 (Find 'SidebarSearchBox').SetFocus()
 [DeleteTestNative]::SetForegroundWindow($script:handle) | Out-Null
 Start-Sleep -Milliseconds 200
 if([DeleteTestNative]::GetForegroundWindow() -ne $script:handle){throw 'Pointer/keyboard input blocked: another application is foreground.'}
}
function Pointer($bounds,[switch]$Right) {
 Foreground
 [DeleteTestNative]::SetCursorPos([int]($bounds.Left+$bounds.Width/2),[int]($bounds.Top+$bounds.Height/2)) | Out-Null
 if($Right){[DeleteTestNative]::mouse_event(8,0,0,0,[IntPtr]::Zero);[DeleteTestNative]::mouse_event(16,0,0,0,[IntPtr]::Zero)}
 else{[DeleteTestNative]::mouse_event(2,0,0,0,[IntPtr]::Zero);[DeleteTestNative]::mouse_event(4,0,0,0,[IntPtr]::Zero)}
 Start-Sleep -Milliseconds 500
}
function Keys($keys) {Foreground;[Windows.Forms.SendKeys]::SendWait($keys);Start-Sleep -Milliseconds 500}
function Screenshot($name) {
 Foreground
 $b=$script:window.Current.BoundingRectangle
 $bitmap=[Drawing.Bitmap]::new([int]$b.Width,[int]$b.Height)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 try {$graphics.CopyFromScreen([int]$b.Left,[int]$b.Top,0,0,$bitmap.Size);$bitmap.Save((Join-Path $output ($name+'.png')))}
 finally{$graphics.Dispose();$bitmap.Dispose()}
}
function Launch {
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$data,'Process')
 $script:process=Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$oldData,'Process')
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:process.Id)
 $script:window=$null
 for($i=0;$i -lt 80;$i++) {
  $script:window=[Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
  if($script:window){break};Start-Sleep -Milliseconds 100
 }
 if(!$script:window){throw 'Test window missing.'}
 $script:handle=[IntPtr]$script:window.Current.NativeWindowHandle
 Foreground
 Start-Sleep -Milliseconds 700
}
function Close-Test {
 if($script:process -and !$script:process.HasExited){
  $script:process.CloseMainWindow() | Out-Null
  if(!$script:process.WaitForExit(5000)){throw 'Test application did not close.'}
 }
}
function Delete-Menu {
 $condition=[Windows.Automation.AndCondition]::new(
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,(Join-Path $data 'scripts')),
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TreeItem))
 $root=$script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
 if(!$root){throw 'Script directory node missing.'}
 $root.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Start-Sleep -Milliseconds 700
 $condition=[Windows.Automation.AndCondition]::new(
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'review_sample.py'),
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TreeItem))
 $file=$script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
 if(!$file){throw 'Script file node missing.'}
 Pointer $file.Current.BoundingRectangle -Right
 $delete=Find 'TreeDeleteScriptFile'
 if($delete.Current.Name -ne '删除脚本'){throw 'Delete menu text is missing.'}
 Screenshot 'delete-menu'
 $delete.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Start-Sleep -Milliseconds 700
}
try {
 & dotnet run --project .\PyRunner.Verification\PyRunner.Verification.csproj -c Debug --no-build -- --prepare-review-ui $data
 if($LASTEXITCODE -ne 0){throw 'Fixture creation failed.'}
 $file=Join-Path $data 'scripts\review_sample.py'
 Launch
 $stopBounds=(Find 'StopButton').Current.BoundingRectangle
 (Find 'RunButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Start-Sleep -Seconds 2
 Delete-Menu
 Screenshot 'running-protected'
 Keys '{ENTER}'
 if(!(Test-Path -LiteralPath $file)){throw 'Running script was deleted.'}
 Pointer $stopBounds
 Start-Sleep -Seconds 3
 Close-Test
 Write-Output 'PASS: running script deletion is blocked'
 Launch
 Delete-Menu
 Screenshot 'delete-confirmation'
 Keys '{ESC}'
 if(!(Test-Path -LiteralPath $file)){throw 'Cancel removed the file.'}
 Close-Test
 Write-Output 'PASS: cancellation preserves the script file'
 Launch
 $codeBounds=(Find 'CodeModeButton').Current.BoundingRectangle
 Pointer $codeBounds
 Start-Sleep -Seconds 2
 Screenshot 'editor-before-delete'
 Delete-Menu
 # Default focus is Cancel. Shift+Tab selects the preceding Delete button.
 Keys '+{TAB}'
 Keys ' '
 Start-Sleep -Seconds 2
 if(Test-Path -LiteralPath $file){throw 'Confirmed deletion did not remove the file.'}
 Screenshot 'after-delete'
 Pointer $codeBounds
 Start-Sleep -Milliseconds 700
 Screenshot 'editor-after-delete'
 Close-Test
 & dotnet run --project .\PyRunner.Verification\PyRunner.Verification.csproj -c Debug --no-build -- --verify-review-delete $data
 if($LASTEXITCODE -ne 0){throw 'Registration/schedule verification failed.'}
 $shell=New-Object -ComObject Shell.Application
 $recycled=$false
 foreach($item in $shell.Namespace(10).Items()) {
  if($item.ExtendedProperty('System.Recycle.DeletedFrom') -eq (Join-Path $data 'scripts') -and $item.Name -like 'review_sample*'){$recycled=$true;break}
 }
 if(!$recycled){throw 'Deleted test file was not found in the Recycle Bin.'}
 Write-Output 'PASS: confirmed deletion places the file in the Recycle Bin'
 Write-Output "Screenshots: $output"
} finally {
 [Environment]::SetEnvironmentVariable('PYRUNNER_DATA_DIRECTORY',$oldData,'Process')
 if($script:process -and !$script:process.HasExited){$script:process.Kill();$script:process.WaitForExit()}
 Pop-Location
}
