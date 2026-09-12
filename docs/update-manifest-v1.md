# PyRunner update manifest schema v1

The stable update manifest is planned for
`https://ethereal-09.github.io/PyRunner/update.json`. The URL is a deployment
target, not a statement that GitHub Pages is currently enabled.

Schema v1 is a UTF-8 JSON object with exactly these top-level properties:

- `schemaVersion`: integer `1`.
- `draft` and `prerelease`: both must be JSON `false`.
- `version`: canonical ASCII `major.minor.patch` product version.
- `tag`: exactly `v{version}`.
- `publishedAt`: UTC ISO-8601 timestamp.
- `releasePageUrl`: the matching stable Release page in
  `Ethereal-09/PyRunner`.
- `releaseNotes`: control-character-filtered plain text, at most 8,000
  characters.
- `assets`: exactly one Windows x64 installer entry.

The asset entry contains `platform`, `architecture`, `fileName`, `url`, `size`
and `sha256`. Its filename is exactly
`PyRunner-Setup-{version}-x64.exe`; its URL is the corresponding HTTPS GitHub
Release download URL; its size is positive and no greater than 256 MiB; and
its SHA-256 is exactly 64 hexadecimal characters.

## Release workflow integration contract

The repository uses the manually dispatched, dedicated
`.github/workflows/release.yml` workflow. The historical v1.1.0 Release was
published by the repository owner after a read-only CI run and is not modified
by this workflow integration. `ci.yml` remains ordinary read-only CI. The
dedicated stable-release sequence is:

1. Build and run verification.
2. Build the installer and SHA-256 asset.
3. Create a draft Release and upload all assets.
4. Publish the stable Release and verify that publication succeeded.
5. Download the installer back from that Release, then invoke
   `tools/release/New-UpdateManifest.ps1` with both the local installer and the
   separately downloaded copy, plus the local and separately downloaded
   `.sha256` assets. The generator verifies exact names, sizes, checksum-file
   format and SHA-256 values and rejects version rollback.
6. Upload the generated site as a Pages artifact and deploy it in a separate
   job with only `contents: read`, `pages: write` and `id-token: write`.

Only the real Release-creation job may receive `contents: write`. Ordinary CI
and pull-request jobs remain read-only. A failed verification, manifest
generation, Pages artifact upload or Pages deployment must not replace the
previous Pages deployment. If the Release is already public when Pages fails,
the operational state is explicitly “Release published; Pages manifest still
points to the previous stable version” and must be repaired before announcing
in-app availability.
