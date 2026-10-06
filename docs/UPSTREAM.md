# Working with the Panasonic reader

The original project is [leecher1337/panasonic-rec](https://github.com/leecher1337/panasonic-rec). DIGA records a Git submodule under `third_party/panasonic-rec`, pointing to [your fork](https://github.com/lukasz-gratkowski/panasonic-rec). This keeps authorship/history, allows your own changes, and makes every build reproducible from a commit pin.

```powershell
git clone --recurse-submodules https://github.com/lukasz-gratkowski/AmgDigaArchive.git
cd AmgDigaArchive
git submodule update --init --recursive
```

## Make a change

```powershell
cd third_party/panasonic-rec
git switch -c diga/my-change
# Edit and test the C sources.
git add .
git commit -m "Describe the Panasonic reader change"
git push -u origin diga/my-change
cd ../..
git add third_party/panasonic-rec
git commit -m "Pin Panasonic reader with my change"
```

The submodule commit must be pushed to the fork before the parent commit is built remotely. Opening a pull request in your fork is recommended. A parent commit records the exact submodule commit, regardless of the branch name.

## Incorporate the author's updates

```powershell
cd third_party/panasonic-rec
# Run once per clone; omit if the remote already exists.
git remote add upstream https://github.com/leecher1337/panasonic-rec.git
git fetch upstream
git switch -c diga/update-upstream
git merge upstream/master
# Resolve conflicts and run the native + managed regression suites.
git push -u origin diga/update-upstream
cd ../..
git add third_party/panasonic-rec
git commit -m "Update Panasonic reader upstream pin"
```

Use a new branch from your existing custom submodule commit when preserving local modifications. Do not reset your custom branch to upstream. CI and releases use the recorded pin and never `git submodule update --remote`.

## Build and integration

`scripts/Build-Native.ps1` compiles all four upstream programs from C sources using MinGW-w64 GCC: `extract_meihdfs.exe`, `udf_dump.exe`, `dvd-vr-meihdfs.exe`, and `dvd-vr-udf.exe`. Native compilation is required in the standard build and release process. The script records the upstream commit and compiler, and starts every binary to catch loader/dependency failures.

The graphical browser uses a bounds-checked managed adaptation of the filesystem algorithms to list files and seek safely without extracting an entire disk. Native binaries are shipped under `tools/native` for reproducibility and upstream regression comparison. Changes to filesystem logic in C need corresponding review of the managed reader; building new native C alone does not automatically change the managed browser. Parity tests compare native and managed extraction on synthetic fixtures.

The native upstream programs are recovery utilities with historical assumptions. The UI uses its guarded managed reader for untrusted raw disk structures. Original source/license notices and DIGA's GPL obligations are preserved.
