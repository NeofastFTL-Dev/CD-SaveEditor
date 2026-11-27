# CD-SaveEditor

A Cel Damage save editor and LiveSplit autosplitter for [xemu](https://xemu.app). It
reads and writes the game's profile inside the emulator's Xbox HDD image (qcow2 or
raw), unlocks the game's content, moves profiles in and out of the image, and drives a
speedrun timer from the game's own progress.

One self-contained .NET 8 program with a desktop window (Avalonia), a command line and
the splitter, built for Windows, Linux and macOS, plus an Android app that edits the
save on the phone.

## Install

Every download is self-contained: no .NET runtime, no extra packages.

### Windows

Run `CD-SaveEditor-<version>-win-x64-setup.exe`. The wizard installs to
`%LOCALAPPDATA%\Programs\CD-SaveEditor` (or `Program Files` when installed for all
users), adds Start Menu entries, an optional desktop shortcut and an uninstaller in
Apps & Features.

### Linux

* Debian/Ubuntu/Mint/Pop!_OS - `sudo apt install ./CD-SaveEditor-<version>-linux-x64.deb`
  (or `sudo dpkg -i ...`), which installs `/opt/cd-saveeditor`, the `cd-saveeditor`
  command, a menu entry and the icon. Remove with `sudo apt remove cd-saveeditor`.
* Any distribution (Arch, Fedora, openSUSE, ...) - `sudo sh CD-SaveEditor-<version>-linux-x64.run`.
  It installs to `/opt/cd-saveeditor`, links `/usr/local/bin/cd-saveeditor` and adds the
  menu entry. `sudo sh CD-SaveEditor-<version>-linux-x64.run --uninstall` removes it.
* A portable `...-linux-x64.tar.gz` is attached for running straight from a folder.

### macOS

Open `CD-SaveEditor-<version>-osx-<arm64|x64>.dmg` and drag **CD-SaveEditor** onto
Applications. The build is not notarised by Apple, so the first launch needs
right-click -> Open (or `xattr -dr com.apple.quarantine /Applications/CD-SaveEditor.app`).

| Platform | Installer | Portable |
| --- | --- | --- |
| Windows x64 | `...-win-x64-setup.exe` | `...-win-x64-portable.zip` |
| Linux x64 | `...-linux-x64.deb`, `...-linux-x64.run` | `...-linux-x64.tar.gz` |
| macOS Apple Silicon | `...-osx-arm64.dmg` | `...-osx-arm64.app.zip` |
| macOS Intel | `...-osx-x64.dmg` | `...-osx-x64.app.zip` |
| Android 7.0+ | `...-android.apk` | - |

### Android

Sideload `CD-SaveEditor-<version>-android.apk` (Android 7.0 / API 24 or newer). Open the
downloaded file and allow "install unknown apps" for the app that opened it; it is signed
with the standard Android debug key, so it installs from a file but cannot be published
on Google Play as is. The APK carries arm64 (phones) and x86_64 (emulators) binaries.

On the phone, **Choose HDD image** copies the `xbox_hdd.qcow2` into the app, where you can
unlock everything, restore the originals or apply individual lock flags; **Save back**
then replaces the file you picked. So the way to use it on the road is: copy the image
from the PC, edit it on the phone, write it back, copy it to xemu again. The game ISO is
optional and only needed for the lock-flag combination, which patches the game's startup
defaults.

## What it does

* **Finds the profile** in xemu's HDD image by scanning for the game's PSC data.
* **Unlocks everything**: arena modes, weapons and vehicles.
* **Per-flag combinations**: unlock only some categories.
* **Restores the originals** from a baseline saved before the first edit.
* **Patches the game's startup defaults** inside the ISO, because the game rejects a
  profile whose header checksum does not match and rebuilds it from those defaults.
  This is what makes an unlock survive the next boot.
* **Exports profiles** to `.psc` files and **imports them back into the image**, so a
  save can be carried between machines or backed up without booting the game.
* **Splits for speedruns**: watches the Smack Attack win flags and drives LiveSplit,
  without touching the image.

Every write keeps the profile text byte-for-byte the same length, so the save header
and the FATX directory entry stay valid. Before writing, a timestamped copy of the HDD
image is made next to it (`*.bak-YYYYMMDD-HHMMSS`), and the first edit keeps the
untouched profile in `saves/original`. The ISO is backed up once as `<iso>.orig`.

## Using it

Start `CD-SaveEditor` with no arguments for the window: pick a save slot, unlock,
restore, export or write a profile back into xemu, or tick individual lock flags.

The same operations are on the command line:

```
CD-SaveEditor                       open the desktop window
CD-SaveEditor status                show the saves and the game file state
CD-SaveEditor export [--out DIR]    write the profiles out as .psc files
CD-SaveEditor import FILE [--slot N]
                                    write a .psc file into the xemu HDD image
CD-SaveEditor unlock [--dry-run]    unlock everything, in place
CD-SaveEditor restore [--dry-run]   put the original values back
CD-SaveEditor split ...             LiveSplit autosplitter (see below)
```

Options: `--image FILE` (HDD image), `--iso FILE` (game ISO), `--out DIR`, `--slot N`,
`--no-backup`, `--dry-run`.

Both paths default to xemu's own configuration (`hdd_path` / `dvd_path` in
`xemu.toml`), so in the usual setup no options are needed. `CELCD_IMAGE` and
`CELCD_ISO` override them.

After `unlock`, `import` or `restore`, start the game once: it reads the profile and
writes it back with a checksum it accepts.

### Where the tool keeps its files

`saves/` sits next to the executable: `saves/original` holds the untouched profiles
with `manifest.json`, and `saves/game_patch.json` records the original ISO config
bytes. While running from a build tree the repository root is used instead. Set
`CD_SAVEEDITOR_DATA` to force a different folder.

## Export and import

`export` writes `pitaPsc_slot1.psc`, `pitaPsc_slot2.psc`, ... (the profile exactly as
the game stores it: 24-byte header plus text).

`import` writes such a file back into the HDD image at the chosen slot. The text must
be exactly as long as the slot's current profile, which is always true for a file
exported from the same slot; the game validates the header, so nothing is rewritten or
re-padded. A different length is refused rather than corrupting the profile - use
`unlock` or the per-flag toggles for changes inside the image.

## Game files

The game's "new profile" defaults live in the `// NEW GAME CONFIG` block of the startup
script packed into `default.xbe` inside the ISO. `unlock` writes zeroes there, so the
game rebuilds the profile unlocked and agrees with it; `restore` puts the original
block back from `saves/game_patch.json`. Everything else in the block (render and movie
settings) is left untouched.

## LiveSplit autosplitter

`CD-SaveEditor split` watches the Smack Attack win flags in the profile and sends
`splitOrStart` to the timer. It only reads the HDD image, so it can run next to xemu.

```
CD-SaveEditor split --dry-run                    # print the timer commands instead
CD-SaveEditor split --serve 16834                # LiveSplit One -> ws://localhost:16834
CD-SaveEditor split --connect 127.0.0.1:16834    # LiveSplit -> Control -> Start TCP Server
CD-SaveEditor split --serve 16834 --demo         # start, two splits, reset: wiring check
CD-SaveEditor split --lss "All Smacks.lss"       # rewrite the splits file
```

Split options: `--event N` (0 smack, 1 race, 2 flag), `--interval SECONDS`, `--demo`.

`All Smacks.lss` is generated from the same stage table the watcher uses: ten splits,
one per Smack Attack stage that can record a win (`arenaWonFlags<arena>0`). The game
ships twelve arenas, so Space 2 (`Lunarcy`) and Space 3 (`Shooting Stars`) have no win
field and insert no split. Race and flag wins never split either.

## Build from source

Requires the .NET 8 SDK:

```
dotnet build src/CD-SaveEditor/CD-SaveEditor.csproj
dotnet run --project src/CD-SaveEditor        # opens the window
dotnet run --project tests/CD-SaveEditor.Tests
```

Packaging locally:

```
dotnet publish src/CD-SaveEditor/CD-SaveEditor.csproj -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux-x64
bash installer/linux/build-deb.sh publish/linux-x64 1.2.0 amd64 dist/CD-SaveEditor.deb
bash installer/linux/build-run.sh publish/linux-x64 1.2.0 dist/CD-SaveEditor.run
```

`installer/windows/CD-SaveEditor.iss` is the Inno Setup script (CI runs it with
`/DMyAppVersion=...`), and `installer/macos/make-dmg.sh` builds the `.app` bundle and
the `.dmg` on macOS.

The Android app needs the Android workload and SDK (the build targets
`net10.0-android` because that is what the current Avalonia Android package supports):

```
dotnet workload install android
dotnet publish src/CD-SaveEditor.Android/CD-SaveEditor.Android.csproj -c Release \
    -f net10.0-android -p:AndroidPackageFormat=apk -o dist/android
```

The phone layout is an Avalonia view shared with the desktop build, so it can be looked
at without a device: `dotnet run --project src/CD-SaveEditor -- --mobile-screenshot
phone.png --image <image>` renders it in a phone-sized window.

`.github/workflows/build.yml` runs the smoke tests on every push, builds the Windows
setup, the Linux `.deb`/`.run`, the macOS `.dmg` for both architectures and the Android
APK, and attaches everything to a GitHub release when a tag such as `v1.3.0` is pushed.

## Notes and limits

* The qcow2 writer patches clusters that are already allocated and uncompressed, which
  is how xemu stores the HDD. A compressed save cluster is reported instead of being
  rewritten.
* Only the profile is edited: no game data or unlock flags outside the save format are
  invented.
* Tested against the US release of Cel Damage in xemu. The tool never modifies the game
  ISO without first writing `<iso>.orig`.
