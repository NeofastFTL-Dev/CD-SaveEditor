# LiveSplit bridge (Python)

This folder is the speedrun timer bridge, not the save editor. `CD-SaveEditor` (the C#
app in `../src`) is the editor; these files only stay in Python because the bridge has
been used and tested as-is.

* `livesplit.py` - watches the profile in the HDD image and sends `splitOrStart` to
  LiveSplit One (WebSocket) or LiveSplit's TCP server. Reads the disk image read-only,
  so it can run next to xemu.
* `app.py` - the original Python editor. It is still here because `livesplit.py` imports
  its image access (`open_image`, `find_saves`, path resolution). Its own CLI still
  works, but `CD-SaveEditor` supersedes it.
* `All Smacks.lss` - ten splits, one per Smack Attack stage that can record a win.

The split list is derived from the save schema: `arenaWonFlags<arena><event>` covers ten
arena digits, while the game ships twelve arenas, so Space 2 (`Lunarcy`) and Space 3
(`Shooting Stars`) have no win field and no split.

```
python3 livesplit.py --serve 16834      # LiveSplit One -> ws://localhost:16834
python3 livesplit.py --dry-run          # watch without a timer
python3 livesplit.py --lss "All Smacks.lss"
```
