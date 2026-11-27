#!/usr/bin/env python3
"""
Cel Damage save tool for xemu Xbox HDD images.

Cel Damage keeps its profile in a small plain-text PSC file
(E:\\UDATA\\45410011\\<slot>\\pitaPsc.psc) with a 24-byte header:

    16 bytes  save id
     u32      checksum over the text that follows
     u32      length of the text that follows

The text is a list of "key value" lines, e.g.

    weaponLockFlags -8388611
    vehicleLockFlags -64
    arenaRaceLockFlags -1
    arenaWonFlags00 0

For the *LockFlags fields a set bit means LOCKED, so 0 unlocks everything.
arenaWonFlagsNN are per-arena/per-mode "already won" bits (set = won).

The game checks that checksum when it loads the profile and silently rebuilds
the file from its built-in defaults if it does not match - which is why
editing the text alone never survives a boot. Those defaults are in the
"NEW GAME CONFIG" block of the startup script packed into default.xbe
(smallFiles.dat inside the ISO), so this tool patches that block as well:
with every lock flag 0 there, the game rebuilds the profile unlocked and
writes a checksum that matches, so the state survives every later boot.

Usage:
    python3 app.py                    # start the local web UI
    python3 app.py status            # show saves and the game file state
    python3 app.py extract           # dump saves to ./saves
    python3 app.py unlock            # unlock everything, in place
    python3 app.py unlock --dry-run  # show the new values only
    python3 app.py restore           # put the original values back
    python3 app.py --image FILE --iso FILE ...

Every save edit keeps the text byte-for-byte the same length, so the header
length field and the FATX directory entry stay valid. The first unlock keeps
the untouched profile in ./saves/original (manifest.json) and the untouched
game ISO as <iso>.orig, recording the original config bytes in
./saves/game_patch.json. `restore` reverts both. Start the game once after
unlock or restore: it rewrites the profile from the (patched or original)
defaults on the next boot.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import struct
import sys
import time
import webbrowser
import zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
SAVE_DIR = os.path.join(HERE, "saves")
BASELINE_DIR = os.path.join(SAVE_DIR, "original")
NEEDLE = b"weaponLockFlags"
HEADER_LEN = 24
DEFAULT_TOML = os.path.expanduser("~/.var/app/app.xemu.xemu/data/xemu/xemu/xemu.toml")
GAME_PATCH_FILE = os.path.join(SAVE_DIR, "game_patch.json")

# The game's own "new profile" defaults, stored as a script in default.xbe
# (inside smallFiles.dat in the ISO). Patching this is what makes an unlock
# survive the next boot: the game then rebuilds the profile with a checksum
# that it accepts.
CONFIG_MARKER = b"// NEW GAME CONFIG"
CONFIG_WINDOW = 2048
CONFIG_KEYS = (
    "weaponLockFlags",
    "vehicleLockFlags",
    "arenaRaceLockFlags",
    "arenaFlagLockFlags",
    "arenaSmackLockFlags",
)

# Fields the tool flips, in unlock order. Fixed-width zero padding keeps the
# file size identical to what the game wrote.
# Content gates only: render/movie settings are left exactly as the game wrote
# them so the display and menu behaviour stay untouched.
UNLOCK_FIELDS = (
    "weaponLockFlags",
    "vehicleLockFlags",
    "arenaRaceLockFlags",
    "arenaFlagLockFlags",
    "arenaSmackLockFlags",
)
WON_PREFIX = "arenaWonFlags"


# --------------------------------------------------------------------------
# Image access: qcow2 (what xemu uses for the HDD) and raw
# --------------------------------------------------------------------------

class Image:
    """Read/write access to a flat guest disk image.

    The file is opened unbuffered, so a handle that outlives a poll always
    returns what is on disk now, including bytes another process wrote after
    the image was opened.
    """

    path = ""
    size = 0

    def _read_exact(self, offset, length):
        self.f.seek(offset)
        out = b""
        while len(out) < length:
            chunk = self.f.read(length - len(out))
            if not chunk:
                break
            out += chunk
        return out

    def read_at(self, offset, length):
        raise NotImplementedError

    def write_at(self, offset, data):
        raise NotImplementedError

    def close(self):
        pass


class RawImage(Image):
    def __init__(self, path):
        self.path = path
        self.f = open(path, "r+b", buffering=0)
        self.size = os.path.getsize(path)

    def read_at(self, offset, length):
        return self._read_exact(offset, length)

    def write_at(self, offset, data):
        self.f.seek(offset)
        self.f.write(data)

    def close(self):
        self.f.close()


class QCow2Image(Image):
    """Minimal qcow2 v2/v3 reader + in-place writer.

    Writes only into clusters that are already allocated and uncompressed,
    which is all we need for patching a save in place.
    """

    def __init__(self, path):
        self.path = path
        self.f = open(path, "r+b", buffering=0)
        h = self._read_exact(0, 112)
        magic, self.version = struct.unpack(">II", h[0:8])
        if magic != 0x514649FB:
            raise ValueError("not a qcow2 image")
        self.cluster_bits = struct.unpack(">I", h[20:24])[0]
        self.size = struct.unpack(">Q", h[24:32])[0]
        self.l1_size = struct.unpack(">I", h[36:40])[0]
        l1_off = struct.unpack(">Q", h[40:48])[0]
        self.csize = 1 << self.cluster_bits
        self.csize_shift = 40 - self.cluster_bits
        self.csize_mask = (1 << (self.cluster_bits - 8)) - 1
        self.offset_mask = (1 << self.csize_shift) - 1
        self.l2_bits = self.cluster_bits - 3
        self.l1 = struct.unpack(">" + "Q" * self.l1_size,
                                self._read_exact(l1_off, self.l1_size * 8))
        self._l2 = {}

    def _l2_table(self, l1_index):
        if l1_index in self._l2:
            return self._l2[l1_index]
        off = self.l1[l1_index] & 0x00FFFFFFFFFFFFFF
        table = None
        if off:
            raw = self._read_exact(off, self.csize)
            table = struct.unpack(">" + "Q" * (self.csize // 8), raw)
        self._l2[l1_index] = table
        return table

    def _entry(self, guest_offset):
        cluster = guest_offset >> self.cluster_bits
        l1_index = cluster >> self.l2_bits
        l2_index = cluster & ((1 << self.l2_bits) - 1)
        table = self._l2_table(l1_index)
        if table is None:
            return None
        return table[l2_index]

    def read_at(self, offset, length):
        out = bytearray()
        while length > 0:
            within = offset & (self.csize - 1)
            n = min(length, self.csize - within)
            entry = self._entry(offset)
            if not entry:
                out += b"\x00" * n
            elif entry & (1 << 62):
                coff = entry & self.offset_mask
                sectors = ((entry >> self.csize_shift) & self.csize_mask) + 1
                blob = self._read_exact(coff, sectors * 512)
                dec = zlib.decompressobj().decompress(blob, self.csize)
                out += dec[within:within + n]
            else:
                coff = entry & 0x00FFFFFFFFFFFFFF
                if entry & 1:
                    out += b"\x00" * n
                else:
                    out += self._read_exact(coff + within, n)
            offset += n
            length -= n
        return bytes(out)

    def write_at(self, offset, data):
        pos = 0
        while pos < len(data):
            here = offset + pos
            within = here & (self.csize - 1)
            n = min(len(data) - pos, self.csize - within)
            entry = self._entry(here)
            if not entry:
                raise RuntimeError(
                    "cannot write to an unallocated region of the qcow2 image "
                    "(offset %#x)" % here)
            if entry & (1 << 62):
                raise RuntimeError(
                    "the save lives in a compressed qcow2 cluster, which cannot "
                    "be patched in place (offset %#x)" % here)
            coff = entry & 0x00FFFFFFFFFFFFFF
            self.f.seek(coff + within)
            self.f.write(data[pos:pos + n])
            pos += n

    def close(self):
        self.f.close()


def open_image(path):
    with open(path, "rb") as f:
        magic = f.read(4)
    if magic == b"QFI\xfb":
        return QCow2Image(path)
    return RawImage(path)


# --------------------------------------------------------------------------
# Save discovery / parsing
# --------------------------------------------------------------------------

def parse_psc(text):
    """Parse a PSC text blob into [(key, value)] while tolerating odd lines."""
    fields = []
    for line in text.split("\r\n"):
        line = line.strip()
        if not line or " " not in line:
            continue
        key, _, raw = line.partition(" ")
        try:
            value = int(raw.strip())
        except ValueError:
            continue
        fields.append((key, value))
    return fields


def try_save_at(img, text_off):
    if text_off < HEADER_LEN:
        return None
    header = img.read_at(text_off - HEADER_LEN, HEADER_LEN)
    length = struct.unpack("<I", header[20:24])[0]
    if not 64 <= length <= 1 << 16:
        return None
    raw = img.read_at(text_off, length)
    if not raw.endswith(b"\r\n"):
        return None
    try:
        text = raw.decode("latin-1")
    except UnicodeDecodeError:
        return None
    fields = parse_psc(text)
    keys = {k for k, _ in fields}
    if "weaponLockFlags" not in keys or "vehicleLockFlags" not in keys:
        return None
    return {
        "text_offset": text_off,
        "header": header,
        "length": length,
        "text": text,
        "fields": fields,
    }


def find_saves(img, progress=None):
    saves = []
    seen = set()
    chunk = 8 << 20
    pos = 0
    tail = b""
    while pos < img.size:
        n = min(chunk, img.size - pos)
        buf = tail + img.read_at(pos, n)
        base = pos - len(tail)
        start = 0
        while True:
            hit = buf.find(NEEDLE, start)
            if hit < 0:
                break
            off = base + hit
            if off not in seen:
                found = try_save_at(img, off)
                if found:
                    seen.add(off)
                    saves.append(found)
                    start = hit + len(NEEDLE)
                    continue
            start = hit + 1
        tail = buf[-HEADER_LEN - len(NEEDLE):]
        pos += n
        if progress:
            progress(min(1.0, pos / img.size))
    saves.sort(key=lambda s: s["text_offset"])
    return saves


def save_name(index, total):
    return "pitaPsc_slot%d.psc" % (index + 1)


def dump_save(save, path):
    with open(path, "wb") as f:
        f.write(save["header"] + save["text"].encode("latin-1"))


def write_baselines(saves):
    """Keep a copy of the untouched saves in ./saves/original.

    Entries are never overwritten: once a save has a baseline, later unlocks
    will not replace it with an already-unlocked version.
    """
    os.makedirs(BASELINE_DIR, exist_ok=True)
    manifest_path = os.path.join(BASELINE_DIR, "manifest.json")
    try:
        with open(manifest_path, "r", encoding="utf-8") as f:
            manifest = json.load(f)
    except (OSError, ValueError):
        manifest = []
    known = {m.get("offset") for m in manifest}
    written = []
    for i, save in enumerate(saves):
        if save["text_offset"] in known:
            continue
        name = save_name(i, len(saves))
        dump_save(save, os.path.join(BASELINE_DIR, name))
        manifest.append({
            "offset": save["text_offset"],
            "length": save["length"],
            "file": name,
        })
        known.add(save["text_offset"])
        written.append(name)
    if written or not os.path.exists(manifest_path):
        tmp = manifest_path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(manifest, f, indent=2)
        os.replace(tmp, manifest_path)
    return written


def load_baselines():
    """Read the saved originals written by write_baselines()."""
    manifest_path = os.path.join(BASELINE_DIR, "manifest.json")
    try:
        with open(manifest_path, "r", encoding="utf-8") as f:
            manifest = json.load(f)
    except (OSError, ValueError):
        return []
    out = []
    for entry in manifest:
        path = os.path.join(BASELINE_DIR, entry.get("file", ""))
        try:
            with open(path, "rb") as f:
                data = f.read()
        except OSError:
            continue
        text = data[HEADER_LEN:].decode("latin-1")
        out.append({
            "offset": entry.get("offset"),
            "file": path,
            "header": data[:HEADER_LEN],
            "text": text,
            "fields": parse_psc(text),
        })
    return out


# --------------------------------------------------------------------------
# Unlock
# --------------------------------------------------------------------------

def unlocked_fields(fields):
    """Return the unlocked field list, sizes preserved."""
    out = []
    for key, value in fields:
        if key in UNLOCK_FIELDS:
            new = 0
        elif key.startswith(WON_PREFIX):
            new = 1
        else:
            out.append((key, value))
            continue
        out.append((key, new))
    return out


def render_psc(fields, original_text):
    """Re-render the text, padding values to their original width."""
    widths = {}
    for line in original_text.split("\r\n"):
        if " " in line:
            key, _, raw = line.partition(" ")
            widths.setdefault(key, len(raw))
    lines = []
    for key, value in fields:
        text = str(value)
        width = widths.get(key, len(text))
        if len(text) < width:
            # Keep the byte length identical so the save header stays valid.
            if value >= 0:
                text = text.rjust(width, "0")
            else:
                text = "0" * width  # only 0 is ever written for negatives
        elif len(text) > width:
            raise ValueError(
                "value %s for %s does not fit the original %d-byte field"
                % (value, key, width))
        lines.append(key + " " + text)
    return "\r\n".join(lines) + "\r\n"


def is_unlocked(fields):
    locks = [v for k, v in fields if k in UNLOCK_FIELDS]
    return bool(locks) and all(v == 0 for v in locks)


def backup(path):
    stamp = time.strftime("%Y%m%d-%H%M%S")
    dst = "%s.bak-%s" % (path, stamp)
    shutil.copyfile(path, dst)
    return dst


def unlock_image(img, saves, dry_run=False, do_backup=True, progress=None):
    """Patch every discovered save to the unlocked state."""
    results = []
    plan = []
    for i, save in enumerate(saves):
        new_fields = unlocked_fields(save["fields"])
        new_text = render_psc(new_fields, save["text"])
        if len(new_text) != len(save["text"]):
            raise RuntimeError("internal error: text length changed")
        plan.append((save, new_text, new_fields))

    if dry_run:
        for save, new_text, new_fields in plan:
            results.append({
                "offset": save["text_offset"],
                "before": save["fields"],
                "after": new_fields,
                "written": False,
            })
        return results, None, []

    baselines = write_baselines([save for save, _, _ in plan])

    backup_path = None
    if do_backup and plan:
        backup_path = backup(img.path)
        progress and progress(0.0)

    for n, (save, new_text, new_fields) in enumerate(plan):
        img.write_at(save["text_offset"], new_text.encode("latin-1"))
        results.append({
            "offset": save["text_offset"],
            "before": save["fields"],
            "after": new_fields,
            "written": True,
        })
        progress and progress((n + 1) / len(plan))
    return results, backup_path, baselines


def apply_combination(img, saves, target, dry_run=False):
    """Write a custom set of lock-flag values into the profile(s).

    The text is only rewritten when the new values fit the original field
    widths; otherwise the header checksum is broken so the game rebuilds the
    profile from the (already patched) startup config on the next boot.
    """
    results = []
    for save in saves:
        fields = [(key, target.get(key, value)) for key, value in save["fields"]]
        try:
            new_text = render_psc(fields, save["text"])
        except ValueError:
            new_text = None
        fits = new_text is not None and len(new_text) == len(save["text"])
        changed = fits and new_text != save["text"]
        if not dry_run:
            if changed:
                img.write_at(save["text_offset"], new_text.encode("latin-1"))
            elif not fits:
                img.write_at(save["text_offset"] - 8, b"\x00\x00\x00\x00")
        results.append({
            "offset": save["text_offset"],
            "before": save["fields"],
            "after": fields,
            "written": bool(changed),
            "mode": "bytes" if fits else "rebuild",
        })
    return results


def restore_image(img, saves, baselines, dry_run=False, do_backup=True, progress=None):
    """Write the saved original values back into every matching save."""
    results = []
    plan = []
    by_offset = {b["offset"]: b for b in baselines if b["offset"] is not None}
    for i, save in enumerate(saves):
        base = by_offset.get(save["text_offset"])
        if base is None and i < len(baselines):
            base = baselines[i]
        if base is None:
            continue
        plan.append((save, base))

    if not plan:
        raise RuntimeError("no saved original in %s" % BASELINE_DIR)

    # A profile the game rewrote has a different size and checksum than the
    # saved original. There is nothing to copy back in that case; the game
    # rebuilds the profile from its (now reverted) defaults on the next boot.
    def restorable(save, base):
        return len(base["text"]) == len(save["text"])

    if dry_run:
        for save, base in plan:
            ok = restorable(save, base)
            results.append({
                "offset": save["text_offset"],
                "before": save["fields"],
                "after": base["fields"] if ok else [],
                "written": False,
                "mode": "bytes" if ok else "rebuild",
            })
        return results, None

    backup_path = None
    if do_backup:
        backup_path = backup(img.path)
        progress and progress(0.0)

    for n, (save, base) in enumerate(plan):
        if restorable(save, base):
            img.write_at(save["text_offset"], base["text"].encode("latin-1"))
            mode = "bytes"
            after = base["fields"]
        else:
            # Break the header checksum: the game then rebuilds the profile
            # from its own defaults (now the original ones) on the next boot.
            img.write_at(save["text_offset"] - 8, b"\x00\x00\x00\x00")
            mode = "rebuild"
            after = []
        results.append({
            "offset": save["text_offset"],
            "before": save["fields"],
            "after": after,
            "written": True,
            "mode": mode,
        })
        progress and progress((n + 1) / len(plan))
    return results, backup_path


# --------------------------------------------------------------------------
# Image discovery
# --------------------------------------------------------------------------

def image_from_xemu_config():
    try:
        with open(DEFAULT_TOML, "r", encoding="utf-8") as f:
            for line in f:
                m = re.match(r"\s*hdd_path\s*=\s*'([^']+)'", line)
                if m:
                    return m.group(1)
    except OSError:
        pass
    return None


def resolve_image(explicit=None):
    candidates = []
    if explicit:
        candidates.append(explicit)
    if os.environ.get("CELCD_IMAGE"):
        candidates.append(os.environ["CELCD_IMAGE"])
    cfg = image_from_xemu_config()
    if cfg:
        candidates.append(cfg)
    candidates += [
        os.path.expanduser("~/Music/Xbox-Emulator-Files/hdd/xbox_hdd.qcow2"),
        os.path.expanduser("~/.var/app/app.xemu.xemu/data/xemu/xemu/hdd/xbox_hdd.qcow2"),
    ]
    for path in candidates:
        if path and os.path.exists(path):
            return path
    raise SystemExit(
        "No Xbox HDD image found. Pass --image /path/to/xbox_hdd.qcow2 "
        "or set CELCD_IMAGE.")


# --------------------------------------------------------------------------
# Game files: the startup config the game builds a new profile from
# --------------------------------------------------------------------------

def iso_from_xemu_config():
    try:
        with open(DEFAULT_TOML, "r", encoding="utf-8") as f:
            for line in f:
                m = re.match(r"\s*dvd_path\s*=\s*'([^']+)'", line)
                if m:
                    return m.group(1)
    except OSError:
        pass
    return None


def resolve_iso(explicit=None):
    candidates = []
    if explicit:
        candidates.append(explicit)
    if os.environ.get("CELCD_ISO"):
        candidates.append(os.environ["CELCD_ISO"])
    cfg = iso_from_xemu_config()
    if cfg:
        candidates.append(cfg)
    for path in candidates:
        if path and os.path.exists(path):
            return path
    raise SystemExit(
        "No Cel Damage ISO found. Pass --iso /path/to/game.iso "
        "or set CELCD_ISO.")


def find_config(iso):
    """Locate the game's startup config block inside the ISO."""
    with open(iso, "rb") as f:
        pos = 0
        tail = b""
        while True:
            buf = f.read(4 << 20)
            if not buf:
                return None
            data = tail + buf
            hit = data.find(CONFIG_MARKER)
            if hit >= 0:
                start = pos - len(tail) + hit
                f.seek(start)
                return start, f.read(CONFIG_WINDOW)
            tail = data[-len(CONFIG_MARKER):]
            pos += len(buf)


def parse_config(block):
    """Pull the lock-flag values out of a startup config block."""
    values = {}
    for line in block.split(b"\r\n"):
        for key in CONFIG_KEYS:
            head = key.encode() + b" "
            if not line.startswith(head):
                continue
            token = line[len(head):].strip().split(b" ")[0].decode("latin-1")
            if token.startswith("b."):
                value = 0
                for bit in token[2:].split("."):
                    value |= 1 << int(bit)
            else:
                value = int(token)
            values[key] = value
    return values


def patch_config_block(block, values):
    """Return the config with `values` written, byte length unchanged.

    The lock-flag lines are laid out again as a group so a value can grow
    (back) from "0" to a full lock mask while the block keeps its size.
    """
    lines = block.split(b"\r\n")
    heads = {key: key.encode() + b" " for key in CONFIG_KEYS}
    found = [i for i, line in enumerate(lines)
             if any(line.startswith(head) for head in heads.values())]
    if not found:
        raise RuntimeError("the startup config has no lock-flag lines")
    first, last = found[0], found[-1]
    order = []
    comments = {}
    for i in found:
        line = lines[i]
        for key in CONFIG_KEYS:
            head = heads[key]
            if not line.startswith(head):
                continue
            rest = line[len(head):].strip()
            token = rest.split(b" ")[0]
            comment = rest[len(token):].strip()
            order.append(key)
            if comment:
                comments[key] = comment
            break
    for key in CONFIG_KEYS:
        if key not in order:
            order.append(key)

    old_len = sum(len(lines[i]) for i in range(first, last + 1))
    old_len += 2 * (last - first)
    new_lines = []
    for key in order:
        text = b"%s %d" % (key.encode(), values.get(key, 0))
        if key in comments:
            text += b"  " + comments[key]
        new_lines.append(text)
    new_len = sum(len(t) for t in new_lines) + 2 * (len(new_lines) - 1)
    if new_len > old_len:
        raise RuntimeError(
            "no room in the game startup config for these values")
    new_lines[-1] += b" " * (old_len - new_len)

    new_block = b"\r\n".join(lines[:first] + new_lines + lines[last + 1:])
    if len(new_block) != len(block):
        raise RuntimeError("internal error: config block changed size")
    return new_block


def load_game_patch():
    try:
        with open(GAME_PATCH_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def game_state(iso):
    found = find_config(iso) if iso else None
    if not found:
        return {"path": iso or "", "found": False, "patched": False, "values": {}}
    offset, block = found
    values = parse_config(block)
    return {
        "path": iso,
        "found": True,
        "offset": offset,
        "patched": bool(values) and all(v == 0 for v in values.values()),
        "values": values,
    }


def default_lock_masks(iso):
    """The game's own lock masks, used when a flag is switched off."""
    masks = {}
    for base in load_baselines():
        for key, value in base["fields"]:
            if key in CONFIG_KEYS:
                masks.setdefault(key, value)
    if len(masks) < len(CONFIG_KEYS):
        source = None
        manifest = load_game_patch()
        if manifest and manifest.get("iso") == iso:
            source = bytes.fromhex(manifest["original"])
        else:
            pristine = iso + ".orig"
            found = find_config(pristine if os.path.exists(pristine) else iso)
            source = found[1] if found else None
        if source:
            masks.update(parse_config(source))
    return {key: masks.get(key, -1) for key in CONFIG_KEYS}


def patch_game(iso, values=None, dry_run=False):
    """Make the game build every new profile from `values`."""
    values = {key: 0 for key in CONFIG_KEYS} if values is None else values
    found = find_config(iso)
    if not found:
        raise RuntimeError("no Cel Damage startup config found inside %s" % iso)
    offset, block = found
    new_block = patch_config_block(block, values)
    if new_block == block:
        return {"offset": offset, "changed": False}
    if dry_run:
        return {"offset": offset, "changed": True}
    manifest = load_game_patch()
    known = (manifest and manifest.get("iso") == iso
             and manifest.get("offset") == offset)
    if not known:
        os.makedirs(SAVE_DIR, exist_ok=True)
        backup_iso = iso + ".orig"
        if not os.path.exists(backup_iso):
            shutil.copyfile(iso, backup_iso)
        data = {"iso": iso, "offset": offset, "length": len(block),
                "original": block.hex()}
        tmp = GAME_PATCH_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        os.replace(tmp, GAME_PATCH_FILE)
    with open(iso, "r+b") as f:
        f.seek(offset)
        f.write(new_block)
    return {"offset": offset, "changed": True}


def revert_game(iso, dry_run=False):
    """Put the game's original startup config back."""
    manifest = load_game_patch()
    if not manifest or manifest.get("iso") != iso:
        return {"reverted": False}
    if dry_run:
        return {"reverted": True}
    with open(iso, "r+b") as f:
        f.seek(manifest["offset"])
        f.write(bytes.fromhex(manifest["original"]))
    try:
        os.remove(GAME_PATCH_FILE)
    except OSError:
        pass
    return {"reverted": True}


def progress_printer(label):
    state = {"last": -1}

    def report(frac):
        pct = int(frac * 100)
        if pct != state["last"] and pct % 5 == 0:
            state["last"] = pct
            print("\r%s %3d%%" % (label, pct), end="", file=sys.stderr, flush=True)

    return report


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def describe(fields):
    for key, value in fields:
        print("  %-24s %d" % (key, value))


def describe_game(args):
    try:
        iso = resolve_iso(args.iso)
    except SystemExit:
        print("game: no ISO found (pass --iso)")
        return
    state = game_state(iso)
    print("game: %s" % iso)
    if not state["found"]:
        print("  startup config: not found")
        return
    print("  startup config: %s" % ("patched" if state["patched"] else "original"))
    for key, value in state["values"].items():
        print("    %-22s %d" % (key, value))


def cmd_status(args):
    path = resolve_image(args.image)
    img = open_image(path)
    print("image: %s (%.1f GiB)" % (path, img.size / (1 << 30)))
    saves = find_saves(img, progress_printer("scanning"))
    print("\rfound %d Cel Damage save(s)" % len(saves))
    for i, save in enumerate(saves):
        print("\n%s  @ %#x  (%d bytes of text)"
              % (save_name(i, len(saves)), save["text_offset"], save["length"]))
        describe(save["fields"])
        print("  unlocked: %s" % ("yes" if is_unlocked(save["fields"]) else "no"))
    img.close()
    print()
    describe_game(args)


def cmd_extract(args):
    path = resolve_image(args.image)
    img = open_image(path)
    saves = find_saves(img, progress_printer("scanning"))
    if not saves:
        print("\rno Cel Damage save found in %s" % path)
        return 1
    os.makedirs(SAVE_DIR, exist_ok=True)
    for i, save in enumerate(saves):
        dst = os.path.join(SAVE_DIR, save_name(i, len(saves)))
        dump_save(save, dst)
        print("\rextracted %s -> %s" % (save_name(i, len(saves)), dst))
    img.close()
    return 0


def cmd_unlock(args):
    path = resolve_image(args.image)
    img = open_image(path)
    saves = find_saves(img, progress_printer("scanning"))
    if not saves:
        print("\rno Cel Damage save found in %s" % path)
        return 1
    results, backup_path, baselines = unlock_image(
        img, saves, dry_run=args.dry_run, do_backup=not args.no_backup,
        progress=progress_printer("writing"))
    img.close()
    print("\r%s" % ("dry run - nothing written" if args.dry_run else "patched %d save(s)" % len(results)))
    game_patched = False
    try:
        iso = resolve_iso(args.iso)
    except SystemExit:
        iso = None
    if iso is None:
        print("warning: no game ISO found, so the game will rebuild its profile "
              "on the next boot; pass --iso to patch the game files")
    else:
        game_patched = patch_game(iso, dry_run=args.dry_run)["changed"]
        print("game files: %s" % (
            "will be patched" if args.dry_run and game_patched
            else "patched - new profiles are built unlocked" if game_patched
            else "already patched"))
        print("backup of the untampered ISO: %s.orig" % iso)
    if not args.dry_run:
        print("start the game once: it rewrites the profile with these values "
              "and a checksum it accepts")
    if baselines:
        print("original saved to: %s" % BASELINE_DIR)
    if backup_path:
        print("backup: %s" % backup_path)
    for res in results:
        print("\n@ %#x" % res["offset"])
        for key, value in res["after"]:
            print("  %-24s %d" % (key, value))
    return 0


def cmd_restore(args):
    path = resolve_image(args.image)
    baselines = load_baselines()
    if not baselines:
        print("no saved original in %s - run `unlock` once first" % BASELINE_DIR)
        return 1
    img = open_image(path)
    saves = find_saves(img, progress_printer("scanning"))
    if not saves:
        img.close()
        print("\rno Cel Damage save found in %s" % path)
        return 1
    results, backup_path = restore_image(
        img, saves, baselines, dry_run=args.dry_run,
        do_backup=not args.no_backup, progress=progress_printer("restoring"))
    img.close()
    print("\r%s" % ("dry run - nothing written"
                     if args.dry_run else "restored %d save(s)" % len(results)))
    try:
        iso = resolve_iso(args.iso)
    except SystemExit:
        iso = None
    if iso and revert_game(iso, dry_run=args.dry_run)["reverted"]:
        print("game files: original startup config restored")
    if not args.dry_run:
        print("start the game once so it rebuilds the profile with the "
              "original values")
    if backup_path:
        print("backup: %s" % backup_path)
    for res in results:
        print("\n@ %#x" % res["offset"])
        if res.get("mode") == "rebuild":
            print("  profile was rewritten by the game: it will rebuild the "
                  "original values on the next boot")
        for key, value in res["after"]:
            print("  %-24s %d" % (key, value))
    return 0


# --------------------------------------------------------------------------
# Web UI
# --------------------------------------------------------------------------

PAGE = """<!doctype html>
<html><head><meta charset="utf-8"><title>Cel Damage save tool</title>
<style>
:root{color-scheme:dark}
*{box-sizing:border-box}
body{margin:0;font:15px/1.5 system-ui,sans-serif;background:#14161a;color:#e8eaed}
header{padding:22px 28px;border-bottom:1px solid #262a31;display:flex;gap:14px;align-items:baseline}
h1{font-size:19px;margin:0;letter-spacing:.2px}
header span{color:#8b939e;font-size:13px}
main{padding:24px 28px;max-width:1000px}
.card{background:#1b1e24;border:1px solid #262a31;border-radius:10px;padding:18px 20px;margin-bottom:18px}
.card h2{font-size:14px;text-transform:uppercase;letter-spacing:.08em;color:#8b939e;margin:0 0 12px}
button{font:inherit;padding:9px 16px;border-radius:8px;border:1px solid #2f3540;background:#232830;color:#e8eaed;cursor:pointer}
button.primary{background:#2f7d4f;border-color:#2f7d4f;color:#fff}
button:disabled{opacity:.45;cursor:default}
.row{display:flex;gap:10px;flex-wrap:wrap;align-items:center}
table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}
td{padding:3px 10px 3px 0;border-bottom:1px solid #232830}
td.k{color:#aeb6c0}
td.v{text-align:right;width:120px}
.changed{color:#7ee2a8}
.ok{color:#7ee2a8}.warn{color:#f0b429}
.combo{display:flex;flex-wrap:wrap;gap:10px;margin-bottom:14px}
.combo label{display:flex;gap:9px;align-items:center;background:#1f242c;border:1px solid #2f3540;border-radius:8px;padding:8px 13px;cursor:pointer;min-width:190px}
.combo label:hover{border-color:#3d4652}
.combo input{width:16px;height:16px;accent-color:#2f7d4f;cursor:pointer}
.combo label span{color:#8b939e;font-size:12.5px;margin-left:auto}
pre{background:#111318;border:1px solid #232830;border-radius:8px;padding:12px;overflow:auto;font-size:12.5px;max-height:260px}
.tabs{display:flex;gap:6px;margin-bottom:10px}
.tabs button{padding:5px 12px;font-size:13px}
.tabs button.active{background:#2f3540;border-color:#3d4652}
small{color:#8b939e}
</style></head><body>
<header><h1>Cel Damage save tool</h1><span id="img"></span></header>
<main>
  <div class="card">
    <h2>1. Save found in the HDD</h2>
    <div class="row" id="slots"></div>
    <p><small id="state"></small></p>
    <div class="row">
      <button id="extract">Extract save</button>
      <button class="primary" id="unlock">Unlock everything</button>
      <button id="restore">Restore original</button>
    </div>
    <p><small>A timestamped copy of the HDD image is kept next to it before writing.</small></p>
  </div>
  <div class="card">
    <h2>2. Game files</h2>
    <p><small id="game"></small></p>
    <p><small>The game rebuilds its profile from these defaults and rejects
    edited saves, so unlocking patches the game's startup config too. Start
    the game once afterwards.</small></p>
  </div>
  <div class="card">
    <h2>3. Lock flags</h2>
    <div class="combo" id="combo"></div>
    <div class="row">
      <button class="primary" id="apply">Apply combination</button>
      <small>Checked = unlocked (0). Unchecked = the game's own lock mask.</small>
    </div>
    <p><small>Saved as the game's defaults too, so it survives the next boot.</small></p>
  </div>
  <div class="card">
    <h2>4. Profile</h2>
    <div class="tabs" id="tabs"></div>
    <table id="flags"></table>
  </div>
</main>
<script>
let state={saves:[],active:0};
const LOCK_KEYS=['weaponLockFlags','vehicleLockFlags','arenaRaceLockFlags','arenaFlagLockFlags','arenaSmackLockFlags'];
const $=s=>document.querySelector(s);
function renderCombo(){
  const s=state.saves[state.active]; if(!s) return;
  const cur=Object.fromEntries(s.fields);
  $('#combo').innerHTML=LOCK_KEYS.map(k=>{
    const open=cur[k]===0;
    return `<label><input type="checkbox" data-key="${k}"${open?' checked':''}> ${k.replace('LockFlags','')}`+
      `<span>${open?'unlocked':'locked ('+cur[k]+')'}</span></label>`;
  }).join('');
  document.querySelectorAll('#combo input').forEach(i=>i.onchange=()=>{
    const span=i.parentElement.querySelector('span');
    span.textContent=i.checked?'unlocked':'locked';
  });
}
function showFlags(){
  const s=state.saves[state.active]; if(!s) return;
  const rows=s.fields.map(([k,v])=>{
    const after=s.after? Object.fromEntries(s.after)[k] : undefined;
    const changed = after!==undefined && after!==v;
    return `<tr><td class="k">${k}</td><td class="v${changed?' changed':''}">${v}${changed?' <span class="changed">\\u2192 '+after+'</span>':''}</td></tr>`;
  }).join('');
  $('#flags').innerHTML=rows;
  $('#tabs').innerHTML=state.saves.map((s,i)=>
    `<button data-i="${i}" class="${i===state.active?'active':''}">slot ${i+1}</button>`).join('');
  document.querySelectorAll('#tabs button').forEach(b=>b.onclick=()=>{state.active=+b.dataset.i;showFlags();});
  renderCombo();
}
async function refresh(){
  const r=await fetch('/api/status'); const j=await r.json();
  state.saves=j.saves;
  $('#img').textContent=j.image;
  $('#slots').innerHTML=j.saves.length? j.saves.map((s,i)=>`<small>slot ${i+1}</small>`).join(' ') : '<span class="warn">no Cel Damage save found</span>';
  const anyUnlocked=j.saves.some(s=>s.unlocked);
  $('#state').textContent=j.saves.length? (anyUnlocked? 'Everything is already unlocked.' : 'Locked content present.') : '';
  const g=j.game||{};
  $('#game').textContent = !g.found ? '' : (g.path+' — startup config: '+
    (g.patched? 'patched, new profiles start unlocked' : 'original (locked defaults)'));
  showFlags();
}
async function post(url,body){
  const r=await fetch(url,body?{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}
                            :{method:'POST'});
  const j=await r.json();
  if(j.error) throw new Error(j.error);
  return j;
}
async function act(button,label,run){
  const b=$(button); b.disabled=true; $('#state').textContent=label;
  try{ const j=await run(); state.saves=j.saves; $('#state').textContent=j.message; showFlags(); }
  catch(e){ $('#state').textContent='failed: '+e.message; }
  b.disabled=false;
}
$('#extract').onclick=()=>act('#extract','extracting...',()=>post('/api/extract'));
$('#unlock').onclick=()=>act('#unlock','writing...',()=>post('/api/unlock'));
$('#restore').onclick=()=>act('#restore','restoring...',()=>post('/api/restore'));
$('#apply').onclick=()=>{
  const want={}; document.querySelectorAll('#combo input').forEach(i=>{want[i.dataset.key]=i.checked;});
  return act('#apply','writing...',()=>post('/api/apply',want));
};
async function boot(){ try{ await refresh(); } catch(e){ setTimeout(boot,800); } }
boot();
</script></body></html>
"""


class Handler(BaseHTTPRequestHandler):
    image = None
    iso = None

    def log_message(self, *args):
        pass

    def _send(self, code, body, ctype="application/json"):
        data = body.encode() if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _state(self, message="", error=""):
        path = resolve_image(Handler.image)
        img = open_image(path)
        saves = find_saves(img)
        img.close()
        try:
            iso = resolve_iso(Handler.iso)
        except SystemExit:
            iso = None
        payload = {
            "image": path,
            "saves": [],
            "game": game_state(iso),
            "message": message,
            "error": error,
        }
        for save in saves:
            payload["saves"].append({
                "offset": save["text_offset"],
                "fields": save["fields"],
                "unlocked": is_unlocked(save["fields"]),
            })
        return payload

    def do_GET(self):
        if self.path in ("/", "/index.html"):
            self._send(200, PAGE, "text/html; charset=utf-8")
        elif self.path == "/api/status":
            try:
                self._send(200, json.dumps(self._state()))
            except Exception as exc:
                self._send(500, json.dumps({"error": str(exc), "saves": []}))
        else:
            self._send(404, json.dumps({"error": "not found"}))

    def do_POST(self):
        try:
            if self.path == "/api/extract":
                path = resolve_image(Handler.image)
                img = open_image(path)
                saves = find_saves(img)
                if not saves:
                    img.close()
                    self._send(200, json.dumps(self._state(error="no save found")))
                    return
                os.makedirs(SAVE_DIR, exist_ok=True)
                names = []
                for i, save in enumerate(saves):
                    dst = os.path.join(SAVE_DIR, save_name(i, len(saves)))
                    dump_save(save, dst)
                    names.append(dst)
                img.close()
                self._send(200, json.dumps(self._state(
                    message="Extracted to " + ", ".join(names))))
            elif self.path == "/api/unlock":
                path = resolve_image(Handler.image)
                img = open_image(path)
                saves = find_saves(img)
                if not saves:
                    img.close()
                    self._send(200, json.dumps(self._state(error="no save found")))
                    return
                results, backup_path, baselines = unlock_image(img, saves)
                img.close()
                msg = "Patched %d save(s)." % len(results)
                if baselines:
                    msg += " Original saved to saves/original."
                if backup_path:
                    msg += " Backup: %s" % os.path.basename(backup_path)
                try:
                    iso = resolve_iso(Handler.iso)
                except SystemExit:
                    iso = None
                if iso:
                    patched = patch_game(iso)["changed"]
                    msg += (" Game files patched." if patched
                            else " Game files were already patched.")
                    msg += " Start the game once so it writes the profile."
                else:
                    msg += (" No game ISO found (pass --iso), so the game will"
                            " rebuild its profile on the next boot.")
                self._send(200, json.dumps(self._state(message=msg)))
            elif self.path == "/api/apply":
                length = int(self.headers.get("Content-Length") or 0)
                try:
                    wanted = json.loads(self.rfile.read(length) or b"{}")
                except ValueError:
                    wanted = {}
                target = {}
                try:
                    iso = resolve_iso(Handler.iso)
                    masks = default_lock_masks(iso)
                except SystemExit:
                    iso = None
                    masks = {key: -1 for key in CONFIG_KEYS}
                for key in CONFIG_KEYS:
                    if key in wanted:
                        target[key] = 0 if wanted[key] else masks[key]
                if not target:
                    self._send(200, json.dumps(self._state(
                        error="no lock flags in the request")))
                    return
                if iso is None:
                    self._send(200, json.dumps(self._state(
                        error="no game ISO found - start the tool with --iso")))
                    return
                path = resolve_image(Handler.image)
                img = open_image(path)
                saves = find_saves(img)
                if not saves:
                    img.close()
                    self._send(200, json.dumps(self._state(error="no save found")))
                    return
                patch_game(iso, values=target)
                results = apply_combination(img, saves, target)
                img.close()
                opened = [k.replace("LockFlags", "") for k in CONFIG_KEYS
                          if target.get(k) == 0]
                msg = ("Unlocked: %s." % ", ".join(opened)) if opened \
                    else "Everything switched back to the game's defaults."
                if any(res["mode"] == "rebuild" for res in results):
                    msg += " The game rebuilds the profile on the next boot."
                msg += " Start the game once so it writes it."
                self._send(200, json.dumps(self._state(message=msg)))
            elif self.path == "/api/restore":
                baselines = load_baselines()
                if not baselines:
                    self._send(200, json.dumps(self._state(
                        error="no original saved yet - unlock once first")))
                    return
                path = resolve_image(Handler.image)
                img = open_image(path)
                saves = find_saves(img)
                if not saves:
                    img.close()
                    self._send(200, json.dumps(self._state(error="no save found")))
                    return
                results, backup_path = restore_image(img, saves, baselines)
                img.close()
                msg = "Restored the original values in %d save(s)." % len(results)
                if any(res.get("mode") == "rebuild" for res in results):
                    msg += " The game rebuilds the profile on the next boot."
                if backup_path:
                    msg += " Backup: %s" % os.path.basename(backup_path)
                try:
                    iso = resolve_iso(Handler.iso)
                except SystemExit:
                    iso = None
                if iso and revert_game(iso)["reverted"]:
                    msg += " Original game startup config restored."
                self._send(200, json.dumps(self._state(message=msg)))
            else:
                self._send(404, json.dumps({"error": "not found"}))
        except Exception as exc:
            self._send(500, json.dumps({"error": str(exc)}))


def cmd_serve(args):
    Handler.image = args.image
    Handler.iso = args.iso
    path = resolve_image(args.image)
    print("image: %s" % path)
    print("open http://127.0.0.1:%d/" % args.port)
    if not args.no_browser:
        webbrowser.open("http://127.0.0.1:%d/" % args.port)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Extract and unlock the Cel Damage save inside an xemu Xbox HDD image.")
    parser.add_argument("--image", help="HDD image (qcow2 or raw); defaults to xemu's hdd_path")
    parser.add_argument("--iso", help="game ISO; defaults to xemu's dvd_path")
    sub = parser.add_subparsers(dest="command")
    p = sub.add_parser("status", help="show the saves found in the image")
    p.set_defaults(func=cmd_status)
    p = sub.add_parser("extract", help="write the save files to ./saves")
    p.set_defaults(func=cmd_extract)
    p = sub.add_parser("unlock", help="unlock everything in place")
    p.add_argument("--dry-run", action="store_true", help="show the result without writing")
    p.add_argument("--no-backup", action="store_true", help="skip the .bak copy of the image")
    p.set_defaults(func=cmd_unlock)
    p = sub.add_parser("restore", help="put the original save values back from ./saves/original")
    p.add_argument("--dry-run", action="store_true", help="show the result without writing")
    p.add_argument("--no-backup", action="store_true", help="skip the .bak copy of the image")
    p.set_defaults(func=cmd_restore)
    p = sub.add_parser("serve", help="start the local web UI (default)")
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--no-browser", action="store_true")
    p.set_defaults(func=cmd_serve)

    args = parser.parse_args(argv)
    if not getattr(args, "func", None):
        args.func = cmd_serve
        args.port = 8765
        args.no_browser = False
    return args.func(args) or 0


if __name__ == "__main__":
    sys.exit(main())
