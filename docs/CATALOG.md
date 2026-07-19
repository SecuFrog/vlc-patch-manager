# Patch catalog

`catalog/patches.json` is the update contract between GitHub and released
manager executables. Schema version 1 contains an array named `patches`.

Required patch fields:

| Field | Meaning |
| --- | --- |
| `id` | Stable, unique patch identifier |
| `name` | User-facing feature name |
| `description` | Short user-facing explanation |
| `patchVersion` | Version of this patch payload |
| `vlcVersion` | Exact four-part VLC file version |
| `architecture` | `x86`, `x64`, or `arm64` |
| `downloadUrl` | HTTPS GitHub URL for the ZIP package |
| `packageSha256` | Uppercase or lowercase SHA-256 of the complete ZIP |
| `payloadEntry` | Plugin file path inside the ZIP |
| `relativeTarget` | Destination below the VLC `plugins` directory |
| `cacheMarker` | Filename expected in VLC's rebuilt plugin cache |

## Adding VLC support

1. Build the plugin against the exact target VLC SDK and architecture.
2. Test playback, seek behavior, cache generation, removal, and rollback.
3. Package the plugin, corresponding source, build instructions, licenses, and
   third-party notices into one ZIP.
4. Calculate the ZIP SHA-256.
5. Upload the immutable ZIP under `patches/` or as a GitHub release asset.
6. Add a catalog entry containing the exact URL and hash.
7. Run the manager integration tests before merging the catalog update.

Never replace a published package at the same URL. Publish a new filename,
patch version, and hash so old catalog revisions remain auditable.
