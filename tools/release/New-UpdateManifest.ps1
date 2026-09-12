param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PublishedAt,
    [Parameter(Mandatory)][string]$ReleaseNotesPath,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$VerifiedRemoteInstallerPath,
    [Parameter(Mandatory)][string]$ChecksumPath,
    [Parameter(Mandatory)][string]$VerifiedRemoteChecksumPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$ExistingManifestPath
)

$ErrorActionPreference = 'Stop'
$maximumInstallerBytes = 256L * 1024 * 1024
$maximumReleaseNotesLength = 8000

if ($Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Version must be an ASCII stable version: $Version"
}
$parsedVersion = [Version]$Version
if ($parsedVersion.ToString(3) -cne $Version) {
    throw "Version must use canonical major.minor.patch form: $Version"
}
$tag = "v$Version"
$expectedName = "PyRunner-Setup-$Version-x64.exe"
$expectedChecksumName = "$expectedName.sha256"

$installer = Get-Item -LiteralPath $InstallerPath -ErrorAction Stop
$remoteInstaller = Get-Item -LiteralPath $VerifiedRemoteInstallerPath -ErrorAction Stop
if ($installer.Name -cne $expectedName -or $remoteInstaller.Name -cne $expectedName) {
    throw "Both installers must use the exact expected name: $expectedName"
}
if ($installer.FullName -eq $remoteInstaller.FullName) {
    throw 'VerifiedRemoteInstallerPath must be a separately downloaded Release asset.'
}
if ($installer.Length -le 0 -or $installer.Length -gt $maximumInstallerBytes -or
    $remoteInstaller.Length -ne $installer.Length) {
    throw 'The local and remote installer sizes are invalid or do not match.'
}
$localHash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash
$remoteHash = (Get-FileHash -LiteralPath $remoteInstaller.FullName -Algorithm SHA256).Hash
if ($localHash -cnotmatch '^[A-F0-9]{64}$' -or $remoteHash -cne $localHash) {
    throw 'The separately downloaded Release asset does not match the local installer SHA-256.'
}
$checksum = Get-Item -LiteralPath $ChecksumPath -ErrorAction Stop
$remoteChecksum = Get-Item -LiteralPath $VerifiedRemoteChecksumPath -ErrorAction Stop
if ($checksum.Name -cne $expectedChecksumName -or $remoteChecksum.Name -cne $expectedChecksumName -or
    $checksum.FullName -eq $remoteChecksum.FullName) {
    throw "Both checksum assets must be separate files named exactly $expectedChecksumName."
}
$expectedChecksumText = "$localHash  $expectedName`n"
$checksumText = [IO.File]::ReadAllText($checksum.FullName).Replace("`r`n", "`n")
$remoteChecksumText = [IO.File]::ReadAllText($remoteChecksum.FullName).Replace("`r`n", "`n")
if ($checksumText -cne $expectedChecksumText -or $remoteChecksumText -cne $expectedChecksumText) {
    throw 'The local or separately downloaded checksum asset is invalid.'
}

$published = [DateTimeOffset]::Parse(
    $PublishedAt,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
if ($published.Offset -ne [TimeSpan]::Zero) {
    throw 'PublishedAt must resolve to UTC.'
}

$rawNotes = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleaseNotesPath))
$notesBuilder = [Text.StringBuilder]::new([Math]::Min($rawNotes.Length, $maximumReleaseNotesLength))
foreach ($character in $rawNotes.ToCharArray()) {
    if ($character -eq "`r" -or $character -eq "`n" -or $character -eq "`t" -or
        -not [char]::IsControl($character)) {
        [void]$notesBuilder.Append($character)
    }
}
$notes = $notesBuilder.ToString().Trim()
if ($notes.Length -gt $maximumReleaseNotesLength) {
    $notes = $notes.Substring(0, $maximumReleaseNotesLength - 1) + '…'
}

if ($ExistingManifestPath -and (Test-Path -LiteralPath $ExistingManifestPath -PathType Leaf)) {
    $existing = Get-Content -LiteralPath $ExistingManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($existing.schemaVersion -ne 1 -or [string]$existing.version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw 'The existing update manifest is invalid; refusing to overwrite it.'
    }
    if ([Version]$existing.version -gt $parsedVersion) {
        throw "Refusing to replace newer stable manifest $($existing.version) with $Version."
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    draft = $false
    prerelease = $false
    version = $Version
    tag = $tag
    publishedAt = $published.ToString('yyyy-MM-ddTHH:mm:ssZ')
    releasePageUrl = "https://github.com/Ethereal-09/PyRunner/releases/tag/$tag"
    releaseNotes = $notes
    assets = @(
        [ordered]@{
            platform = 'windows'
            architecture = 'x64'
            fileName = $expectedName
            url = "https://github.com/Ethereal-09/PyRunner/releases/download/$tag/$expectedName"
            size = $installer.Length
            sha256 = $localHash
        }
    )
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'OutputPath must have a parent directory.'
}
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$temporaryOutput = [IO.Path]::Combine(
    $outputDirectory,
    [string]::Format(
        ".{0}.{1}.tmp",
        [IO.Path]::GetFileName($resolvedOutput),
        [Guid]::NewGuid().ToString('N')))
$json = $manifest | ConvertTo-Json -Depth 5
try {
    [IO.File]::WriteAllText($temporaryOutput, $json + "`n", [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryOutput -Destination $resolvedOutput -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryOutput -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryOutput -Force
    }
}

Get-Item -LiteralPath $resolvedOutput
