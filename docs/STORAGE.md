# Read-only Panasonic storage engine

The storage engine is a managed, bounded port of the actual on-disk algorithms in
[leecher1337/panasonic-rec](https://github.com/leecher1337/panasonic-rec), checked into
`third_party/panasonic-rec`. It does not invoke an undocumented Windows filesystem
driver, mount the disk, initialize it, format it, repair it, or write recorder metadata.

## Supported layouts

* MEIHDFS-V2.0, V2.1, V2.2 and V2.3, including the upstream `HDFS2.x` header spelling.
  Like the upstream implementation, headers are searched every 64 KiB, inode tables
  are discovered in the 128 KiB region starting at superblock + 0xC6000, and block
  runs are resolved using 0xC0000-byte allocation blocks, 0x600-byte run offsets,
  and `length * factor * 512` bytes per allocation run.
* Six inode tables for versions below 2.3, nine for version 2.3; earlier versions
  use the 1980 timestamp epoch and version 2.3 uses Unix time.
* Bounded fallback to repeated backup inode tables every 48 GiB when the primary
  inode has a nonzero file size but no allocation runs. Recovery is reported in
  the filesystem warnings.
* Panasonic's older UDF variant: a File Set Descriptor on a 64 KiB boundary acts
  as the partition start because these recorder disks lack ordinary volume and
  partition descriptors. Strategy 4 file entries, short and long allocation
  descriptors, fragmented files, embedded data, sparse/unrecorded extents,
  8-bit and 16-bit OSTA compressed Unicode filenames are supported.
* UDF descriptor checksums and CRCs are checked. Extended file entries, extended
  allocation descriptors, continuation descriptors and multi-partition mappings
  are explicitly rejected. This is not a general-purpose UDF implementation.

The upstream explicitly mentions the Panasonic DMR-EX768EP-K. It does **not** establish
compatibility with the DMR-BS850 or with external USB disks registered to a particular
recorder. A disk attached through a USB-to-SATA/IDE adapter can expose an older
recorder's internal disk, but that is different from an encrypted recorder USB
recording disk. Encryption and device-binding removal are not implemented. No
actual recorder disk or disk image was supplied for this development; hardware
and model compatibility remain unverified.

## What appears in the recording list

Video candidates are actual files with VRO, VOB, TS, MTS, M2TS, MPG, MPEG or TOD
extensions. The engine lists their filesystem names, sizes and available timestamps.
These are containers, and one VRO can contain more than one recorded program.
`Files` also includes management/IFO files; `Recordings` filters video candidates.
Automatic IFO program title parsing and program splitting are not implemented.
The filesystem warnings expose this limitation to the UI. File extensions alone
do not prove a stream is playable; MediaInfo and FFmpeg inspect the contents.

## Safety and failure behavior

Physical sources are restricted to `\\.\PhysicalDriveN` paths and are opened with
`GENERIC_READ` only. Image files are opened `FileAccess.Read` with sharing that
does not permit simultaneous writes. Native raw reads align to the device's sector
size. Raw access needs Windows administrator rights; ordinary image reading does
not. Enumeration uses metadata-only handles and returns USB classification along
with other physical disks.

Each referenced range must fit the source length. Signed arithmetic overflow,
incomplete files, invalid names, invalid inode references, excessive directory
depth/page count and directory cycles cause clear failures, rather than silently
exporting partial data. Disk disconnects and short reads stop the operation.
Directory metadata has a 128 MiB aggregate budget; retained file metadata has a
64 MiB budget. Directory entries/count and diagnostic messages are also bounded.
The parser does not perform media-sector salvage or invent missing video bytes.
UDF explicitly unrecorded extents are zero-filled according to their allocation
type. It rejects malformed directory entries rather than accepting path traversal.

Extraction uses a unique `.partial` file alongside the requested destination,
checks cancellation throughout the copy, then atomically renames the completed
file without overwriting existing files. Canceled/failed copies remove the partial
file. It rejects the source image path as the destination. When the source is a
physical disk, destination volume disk extents are queried and extraction is
refused if the destination is on that disk, including mounted folders or spanned
volumes; failure to resolve the destination volume is also refused. UNC destinations
are refused for physical sources because a share can resolve back to the source
disk through a local-machine name or DNS alias. Export to another local disk first,
then use cloud upload. File stream lifetime belongs to the
filesystem; disposing the filesystem invalidates its open readers.

## API

```csharp
IReadOnlyList<DiskSource> disks = await DiskDiscovery.GetDisksAsync(token);
using PanasonicFileSystem fs = await PanasonicReader.OpenAsync(path, progress, token);
RecordingFile recording = fs.Recordings[0];
using Stream input = fs.OpenRead(recording); // seekable, read-only extent stream
await fs.ExtractAsync(recording, destination, copyProgress, token);
// Also validate a final remux destination against the original disk, after staging:
DiskDiscovery.EnsureSafeDestination(fs.SourcePath, finalOutputPath);
```

`ScanProgress` reports `BytesScanned`, `TotalBytes` and a human-readable `Message`.
Extraction progress is a fraction from 0 through 1. Each opened stream has its own
cursor, so metadata probing and preview can open independent readers. Cancellation
is checked while scanning headers, traversing directories and copying files.

## Verification

`tests/Diga.Tests/StorageFixtureBuilder.cs` generates synthetic MEIHDFS and Panasonic
UDF images from arbitrary payload bytes. The builder is also usable for an end-to-end
fixture containing real generated MPEG-2 video. Tests cover version/epoch handling,
fragmented extents, seeking, independent cursors, extraction byte fidelity, source
SHA-256 immutability, empty files, cancellation cleanup, overwrite/source refusal,
corrupt sizes, corrupt checksums/CRC, out-of-source extents, cycles, traversal names,
invalid inode IDs and UDF filename/data addressing forms. Synthetic fixtures verify
the implemented algorithm and do not establish actual recorder compatibility.

## Copyright and license provenance

The MEIHDFS layout port derives from `meihdfs/extract/extract_meihdfs.c` and
`meihdfs/extract/meihdfs1.h`:

* Copyright (C) 2012 Honza Maly `<hkmaly@matfyz.cz>`.
* Copyright (C) 2015–2016 `<leecher@dose.0wnz.at>`.
* GNU General Public License, version 2 or (at your option) any later version.

The Panasonic UDF port derives from `udf/pana-udf/udf_fs.c`, `udf_file.c`, and
the `cdio/ecma_167.h` structures:

* Copyright (C) 2005, 2006, 2008 Rocky Bernstein `<rocky@gnu.org>`.
* Panasonic adaptations Copyright (C) 2015 `<leecher@dose.0wnz.at>`.
* GNU General Public License, version 3 or (at your option) any later version.

The combined DIGA application is distributed under GPL-3.0-or-later. The upstream
source and its notices must remain available with corresponding source for binary
distribution. New managed implementation changes: Copyright (C) 2026 DIGA contributors.

The following BSD notice is retained from portions of upstream `udf_fs.c`:

> Portions copyright (c) 2001, 2002 Scott Long <scottl@freebsd.org>
> All rights reserved.
>
> Redistribution and use in source and binary forms, with or without
> modification, are permitted provided that the following conditions are met:
> 1. Redistributions of source code must retain the above copyright notice,
>    this list of conditions and the following disclaimer.
> 2. Redistributions in binary form must reproduce the above copyright notice,
>    this list of conditions and the following disclaimer in the documentation
>    and/or other materials provided with the distribution.
>
> THIS SOFTWARE IS PROVIDED BY THE AUTHOR AND CONTRIBUTORS ``AS IS'' AND
> ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
> IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
> ARE DISCLAIMED. IN NO EVENT SHALL THE AUTHOR OR CONTRIBUTORS BE LIABLE
> FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
> DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
> OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
> HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
> LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
> OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
> SUCH DAMAGE.
