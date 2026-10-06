# DIGA architecture

DIGA is a Windows 11 x64 desktop application using WinUI 3, Windows App SDK 2.5.1 and .NET 10. It reads Panasonic recorder disks through a managed, bounds-checked adaptation of leecher1337/panasonic-rec. It never opens a recorder source for writing.

## Data flow

`Physical USB disk or image -> format detection -> virtual recording file -> local staging -> ffprobe + MediaInfo -> FFmpeg stream copy -> completed local file -> optional cloud upload`

`Local DLNA recorder -> SSDP discovery -> paginated ContentDirectory catalogue -> HTTP media download -> delivered file kept unchanged, or one verified stream-copy container -> Archive/cloud`

Saved files are named by `SavedFileNames` from the session's optional order number and the recording title; cloud uploads reuse the local file name.

DLNA source state is separate from physical recording extents; unknown sizes remain unknown rather than being represented as empty disk files.

The UI uses the same core services as the tests. Disk access, filesystem interpretation, native media tools and network transfer have separate boundaries. Core has no WinUI dependency. Long operations use cancellation and report progress. Physical disk access may need administrator rights; disk images and cloud operations do not require elevation.

### Disk sources

Windows raw disk discovery identifies USB disks. A file image provides an alternative for safer repeatable recovery and testing. Disk length and every metadata-derived offset/extent are checked before reads. The parser derives from the upstream MEIHDFS and Panasonic-specific UDF algorithms, with the original project retained under `third_party/panasonic-rec`. The managed port allows listing and seeking without first dumping the entire disk.

DMR-BS850 model compatibility cannot be inferred solely from the upstream project. Encrypted/paired external recording media, damaged structures and other filesystem variants can be unsupported. No decryption bypass is implemented. See STORAGE.md for the exact parser coverage.

### DLNA sources

`DlnaDiscoveryService` sends bounded IPv4 SSDP searches on active local interfaces and validates device descriptions. `DlnaContentDirectoryClient` browses one folder at a time using SOAP/DIDL, with cancellation, stable pagination checks, response/object budgets and DTDs disabled. The HTTP clients bypass ambient proxies/cookies/credentials and connect only to local/loopback addresses; catalogue endpoints must remain on the recorder host. The app never enables the recorder server or changes firewall settings automatically.

`DlnaDownloadService` streams large recordings to a unique partial file, computes SHA-256, verifies available advertised lengths, then atomically promotes without overwrite. It requests the unchanged representation, rejects protected/converted responses and follows at most three same-host redirects. Cancellation/inactivity removes owned partials. No total-size declaration means completeness cannot be established solely from connection closure. A CI=0 declaration is not independent proof of disk-original equality.

When the delivered stream is kept unchanged, the UI registers the completed download in Archive before metadata inspection so a later failure cannot hide a completed copy. In container mode the download waits in the destination folder under a working name (`.diga-<id>.download<ext>`) and is not listed; it is deleted once the container has passed the FFprobe stream check and the duration check, and otherwise (remux failed or cancelled, durations differ or are unknown, or the working file could not be deleted) it is renamed to its own name (or left under the working name when the rename also fails, as with a file another process holds open) and registered in Archive with the reason. Preview caches have exact session ownership. Source transitions clear source selections and playback identity while preserving exports and cache cleanup ownership. The original Panasonic submodule/build remains the disk recovery engine; DLNA adds no native build dependency. [Setup and verification details](DLNA.md).

### Media processing

MKV is the default archival container. MPEG program stream and MP4 are available only when the original streams are compatible. The export pipeline uses FFmpeg `-c copy` and does not change codecs. MPEG-4 is treated as the MP4 container; converting MPEG-2 video into the MPEG-4 codec would require transcoding and is outside the requested behavior.

MediaInfo's actual native library inspects staged original files and completed destinations. ffprobe supplies machine-readable stream data for compatibility checks. Preview uses Windows playback support; lack of a Windows decoder must not be mistaken for an extraction failure. Metadata and preview can require local staging space. VRO files may contain multiple programs; do not interpret a source-file listing as an IFO program catalogue.

### Cloud

OneDrive is the application's only cloud destination. It uses an installed-application OAuth flow with browser sign-in, PKCE and a loopback redirect, without a client secret. The Microsoft application (client) ID is built in (`AppSettings.BuiltInOneDriveClientId`) and can be replaced in Settings. Tokens are protected for the Windows user. Cloud uploads occur only after the user selects an upload action. OneDrive accepts completed files through its upload API; this version stages/exports locally before upload rather than piping raw recorder data directly to the cloud. The core library also contains Google Drive upload code with its tests; the application no longer exposes it.

### Deployment

An unpackaged self-contained build includes the .NET and Windows App SDK runtimes. Inno Setup creates a Windows 11 per-user installer with Start menu shortcuts and uninstall support. FFmpeg and MediaInfo are bundled from pinned, hashed downloads. The development installer is unsigned; a distributor supplies its own signing certificate. Build scripts and source are included in the workspace.

## Release boundary

Synthetic images and generated media validate algorithms and integration, not actual recorder compatibility. With no recorder disk available, real-device recovery requires subsequent validation. For OneDrive the application registration is built in and the project owner has reported a working sign-in and upload. Clean-VM and interactive accessibility results must be distinguished from local publish and process-launch checks.
