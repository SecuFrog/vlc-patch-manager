# VLC Patch Manager

VLC Patch Manager is a small Windows application that detects installed and
portable VLC copies, retrieves a versioned patch catalog from GitHub, and
installs only patches that match the exact VLC version and PE architecture.

The current catalog contains a native torrent access plugin for VLC 3.0.23
x64. libtorrent runs inside VLC's normal input pipeline; there is no localhost
HTTP server, Lua launcher, helper player, or separate streaming process.

## Use

1. Close VLC.
2. Run `VlcPatchManager.exe`.
3. Select the detected VLC installation, or use Browse for a portable copy.
4. Wait for the GitHub catalog check.
5. Choose Install selected.
6. Open VLC and use Media > Open Network Stream.

The VLC 3 preview currently requires `magnet://?xt=urn:btih:...` compatibility
syntax. Direct `magnet:?` handling requires a VLC core change or VLC 4 module.

## Multiple VLC versions

The manager executable contains no torrent plugin binary. Each catalog entry
declares its VLC version, architecture, package URL, SHA-256 hash, ZIP payload
path, VLC target path, and plugin-cache marker. Compatible entries are selected
automatically. Adding another VLC version means building and testing another
package and adding one catalog entry; the manager does not need to be rebuilt.

See [docs/CATALOG.md](docs/CATALOG.md) for the format and release procedure.

## Safety

- Downloads only catalog packages hosted over HTTPS on GitHub domains.
- Requires a valid SHA-256 package hash before extraction.
- Constrains catalog installation targets to VLC's `plugins` directory.
- Refuses incompatible VLC versions and architectures.
- Backs up collisions and tracks installed hashes.
- Rebuilds and verifies VLC's `plugins.dat`.
- Rolls back failed installation and removal operations.
- Caches catalog metadata for recovery/removal, but never caches payload ZIPs.
- Requests UAC only when the selected VLC directory is not writable.

GitHub account control and HTTPS are currently the trust root for catalog
updates. A future release should add detached catalog signatures and key
rotation before third-party catalogs are supported.

## Build and test

Run `Build.ps1` on Windows. It uses the .NET Framework 4.8 compiler included
with Windows and writes `dist\VlcPatchManager.exe`. No patch payload is embedded.

Run `tests\Run-IntegrationTests.ps1` to exercise downloads, hash validation,
installation, protected and forced removal, original-file restoration, and
cache-failure rollback against a disposable VLC 3.0.23 x64 fixture.

## Legal

Use BitTorrent only for content you are permitted to download and share.
BitTorrent normally uploads pieces to peers while downloading.

The patch manager source is MIT licensed. The patch archive carries its own
LGPL and third-party notices under `licenses/` and `THIRD-PARTY-NOTICES.txt`.
