param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$InnoSetupCompiler,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot 'PyRunner\PyRunner.csproj'
$publishDirectory = Join-Path $projectRoot 'artifacts\publish\win-x64'
$installerScript = Join-Path $PSScriptRoot 'PyRunner.iss'
$compilerCandidates = if ($InnoSetupCompiler) {
    @($InnoSetupCompiler)
} else {
    @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
}
$compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $compiler) {
    throw 'Inno Setup 6 compiler ISCC.exe was not found. Install Inno Setup 6 or pass -InnoSetupCompiler.'
}

[xml]$projectXml = Get-Content -LiteralPath $appProject
$versionNodes = @($projectXml.Project.PropertyGroup.Version | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
if ($versionNodes.Count -ne 1) {
    throw 'PyRunner.csproj must contain exactly one non-empty <Version> element.'
}
$appVersion = ([string]$versionNodes[0]).Trim()
if ($appVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "PyRunner.csproj contains an invalid stable version: $appVersion"
}

dotnet clean $appProject `
    -c $Configuration `
    -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "PyRunner clean failed with exit code $LASTEXITCODE." }

$resolvedProjectRoot = [System.IO.Path]::GetFullPath($projectRoot)
$resolvedPublishDirectory = [System.IO.Path]::GetFullPath($publishDirectory)
$expectedPublishRoot = [System.IO.Path]::GetFullPath((Join-Path $resolvedProjectRoot 'artifacts\publish'))
if (-not $resolvedPublishDirectory.StartsWith($expectedPublishRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear publish directory outside the expected root: $resolvedPublishDirectory"
}
if (Test-Path -LiteralPath $resolvedPublishDirectory) {
    Remove-Item -LiteralPath $resolvedPublishDirectory -Recurse -Force
}

$publishArguments = @(
    'publish', $appProject,
    '-c', $Configuration,
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:Platform=x64',
    '-p:PublishSingleFile=false',
    '-o', $publishDirectory
)
if ($NoRestore) {
    $publishArguments += '--no-restore'
}
dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "PyRunner publish failed with exit code $LASTEXITCODE." }

& $compiler "/DAppVersion=$appVersion" $installerScript
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }

$installer = Join-Path $projectRoot "artifacts\installer\PyRunner-Setup-$appVersion-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer was not generated: $installer" }
$installerItem = Get-Item -LiteralPath $installer
$installerHash = (Get-FileHash -LiteralPath $installerItem.FullName -Algorithm SHA256).Hash
$checksum = $installerItem.FullName + '.sha256'
[IO.File]::WriteAllText(
    $checksum,
    "$installerHash  $($installerItem.Name)`n",
    [Text.ASCIIEncoding]::new())
Get-Item -LiteralPath $installerItem.FullName, $checksum
