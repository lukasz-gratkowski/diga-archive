# AMG DIGA Archive: the disk and disk-image reader (archived)

This branch keeps the last state of AMG DIGA Archive that could read a Panasonic recorder's hard disk, a USB disk or a disk image. It is version 0.5.2, a development prerelease that was not published.

From version 0.6.0 on, the application saves recordings from a recorder on the home network only, and the disk reader is no longer part of it. The current application is on the [`main` branch](https://github.com/lukasz-gratkowski/diga-archive).

## What is here

- The application as it was in 0.5.2: the window (`src/Diga.App`), the library (`src/Diga.Core`) and the tests (`tests/Diga.Tests`).
- The read-only storage engine for the MEIHDFS 2.x / HDFS2 and Panasonic UDF layouts. [docs/STORAGE.md](docs/STORAGE.md) describes it, and [docs/UPSTREAM.md](docs/UPSTREAM.md) says how it relates to the code it derives from.
- The build and packaging scripts of that version, as they were.

## What you should know before using it

- **It is not maintained.** Nothing on this branch is built or tested any more; the workflows were removed from this copy, so no check runs here.
- **The disk reader was tested with generated disk images and by comparison with the upstream tool.** The project has no report of it reading a real recorder disk.
- **It only reads.** The engine opens a disk or an image read-only and contains nothing that decrypts protected recordings.
- **This copy is a single commit.** The earlier history, the project's internal records and the screenshots of that version are not published.

## Credits and licence

The storage engine derives from [leecher1337/panasonic-rec](https://github.com/leecher1337/panasonic-rec); its original C code is referenced as the submodule `third_party/panasonic-rec`. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The code on this branch is licensed under the GNU General Public License, version 3; see [LICENSE](LICENSE).
