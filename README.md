# CD-SaveEditor

A Cel Damage save editor for [xemu](https://xemu.app). It reads and writes the game's
profile inside the emulator's Xbox HDD image (qcow2 or raw), unlocks the game's
content, and can move a profile out of the image and back into it.

The editor is a single self-contained .NET 8 program: a command line and a local web
UI, built for Windows, Linux and macOS.

## Download

Grab the archive for your platform from the
[latest release](https://github.com/NeofastFTL-Dev/CD-SaveEditor/releases) and unpack it
anywhere. Every build is self-contained, so no .NET runtime is needed.

| Platform | Archive | Binary |
| --- | --- | --- |
| Windows | `CD-SaveEditor-win-x64.zip` | `CD-SaveEditor.exe` |
| Linux | `CD-SaveEditor-linux-x64.tar.gz` | `CD-SaveEditor` |
| macOS (Intel) | `CD-SaveEditor-osx-x64.tar.gz` | `CD-SaveEditor` |
| macOS (Apple Silicon) | `CD-SaveEditor-osx-arm64.tar.gz` | `CD-SaveEditor` |

Run the binary without arguments to open the web UI, or use the commands below.

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

Every write keeps the profile text byte-for-byte the same length, so the save header
and the FATX directory entry stay valid. Before writing, a timestamped copy of the HDD
image is made next to it (`*.bak-YYYYMMDD-HHMMSS`), and the first edit keeps the
untouched profile in `saves/original`. The ISO is backed up once as `<iso>.orig`.

## Commands

```
CD-SaveEditor                       start the local web UI (http://127.0.0.1:8765)
CD-SaveEditor status                show the saves and the game file state
CD-SaveEditor export [--out DIR]    write the profiles out as .psc files
CD-SaveEditor import FILE [--slot N]
                                    write a .psc file into the xemu HDD image
CD-SaveEditor unlock [--dry-run]    unlock everything, in place
CD-SaveEditor restore [--dry-run]   put the original values back
CD-SaveEditor serve [--port N] [--no-browser]
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
`unlock` or the web UI's per-flag toggles for changes inside the image.

## Game files

The game's "new profile" defaults live in the `// NEW GAME CONFIG` block of the startup
script packed into `default.xbe` inside the ISO. `unlock` writes zeroes there, so the
game rebuilds the profile unlocked and agrees with it; `restore` puts the original
block back from `saves/game_patch.json`. Everything else in the block (render and movie
settings) is left untouched.

## LiveSplit bridge

`splitter/` still holds the Python LiveSplit bridge and the `All Smacks` splits file
used for the speedrun timer. It is a separate tool (it only reads the HDD image and
needs `app.py` for the image access) and is not part of the editor. See
`splitter/README.md`.

## Build from source

Requires the .NET 8 SDK:

```
dotnet build src/CD-SaveEditor/CD-SaveEditor.csproj
dotnet run --project src/CD-SaveEditor
dotnet run --project tests/CD-SaveEditor.Tests     # smoke tests, no external packages
```

Release archives are produced by `.github/workflows/build.yml`: every push runs the
smoke tests and builds `win-x64`, `linux-x64`, `osx-x64` and `osx-arm64`; pushing a tag
such as `v1.0.0` attaches the four archives to a GitHub release.

## Notes and limits

* The qcow2 writer patches clusters that are already allocated and uncompressed, which
  is how xemu stores the HDD. A compressed save cluster is reported instead of being
  rewritten.
* Only the profile is edited: no game data or unlock flags outside the save format are
  invented.
* Tested against the US release of Cel Damage in xemu. The tool never modifies the game
  ISO without first writing `<iso>.orig`.
