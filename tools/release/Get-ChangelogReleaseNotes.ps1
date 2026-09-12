param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ChangelogPath,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

if ($Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Version must be an ASCII stable version: $Version"
}
$parsedVersion = [Version]$Version
if ($parsedVersion.ToString(3) -cne $Version) {
    throw "Version must use canonical major.minor.patch form: $Version"
}

$resolvedChangelog = Resolve-Path -LiteralPath $ChangelogPath -ErrorAction Stop
$text = [IO.File]::ReadAllText($resolvedChangelog)
$normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
$escapedVersion = [Regex]::Escape($Version)
$pattern = "(?ms)^## \[$escapedVersion\] - \d{4}-\d{2}-\d{2}\s*`n(?<notes>.*?)(?=^## \[|\z)"
$matches = [Regex]::Matches($normalized, $pattern)
if ($matches.Count -ne 1) {
    throw "CHANGELOG.md must contain exactly one dated section for $Version."
}

$notes = $matches[0].Groups['notes'].Value.Trim()
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw "The CHANGELOG.md section for $Version is empty."
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'OutputPath must have a parent directory.'
}
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[IO.File]::WriteAllText($resolvedOutput, $notes + "`n", [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $resolvedOutput
