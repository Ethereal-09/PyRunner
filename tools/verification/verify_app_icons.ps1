$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$solutionRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactsDirectory = Join-Path $solutionRoot 'artifacts'
[xml]$project = Get-Content -LiteralPath (Join-Path $solutionRoot 'PyRunner\PyRunner.csproj')
$version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid PyRunner project version.' }

$targets = @(
    @{
        Source = Join-Path $artifactsDirectory 'publish\win-x64\PyRunner.exe'
        Output = Join-Path $artifactsDirectory 'app-icon-executable.png'
    },
    @{
        Source = Join-Path $artifactsDirectory "installer\PyRunner-Setup-$version-x64.exe"
        Output = Join-Path $artifactsDirectory 'app-icon-installer.png'
    }
)

foreach ($target in $targets) {
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($target.Source)
    if ($null -eq $icon) { throw "No icon found in $($target.Source)" }
    try {
        $bitmap = $icon.ToBitmap()
        try {
            $bitmap.Save($target.Output, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $icon.Dispose()
    }
    Write-Output $target.Output
}
