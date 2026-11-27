#!/usr/bin/env python3
"""LiveSplit bridge for Cel Damage in xemu.

The game records its own progress in pitaPsc.psc on the Xbox HDD: every arena
win shows up as an arenaWonFlags<arena><event> field going from 0 to a value.
Only the Smack Attack ones may split, so a win in Race or Flag Rally cannot
inject a bogus split.

The event digit comes from the game itself: its debug documentation for
DebugEventId says "event id --- 0 smack, 1 race, 2 flag" (default.xbe data
section), and the save carries exactly three flags per arena in that order
(arenaWonFlags00..92).  So Smack Attack sets arenaWonFlags<arena>0.

How many stages can split is not a guess either.  The game ships twelve arena
level packs, but its save only has room for ten arena digits: the engine
registers exactly thirty win fields, arenaWonFlags<arena 0-9><event 0-2>
(default.xbe variable registry, and the same thirty appear in pitaPsc.psc).
Ten arenas therefore record a Smack Attack win and two do not - the last two of
the game's arena table, Space arenas 2 and 3, which have no save field at all.
Any stage list this bridge produces stops at those ten.

LiveSplit One (works on Linux, no Windows needed):
    python3 livesplit.py --serve 16834
    one.livesplit.org -> Connect to Server -> ws://localhost:16834

LiveSplit (Windows, or Wine):
    python3 livesplit.py --connect 127.0.0.1:16834
    LiveSplit -> right click -> Control -> Start TCP Server

Watch it without a timer:
    python3 livesplit.py --dry-run

Check the wiring:  python3 livesplit.py --serve 16834 --demo
Only reads the disk image, so it is safe to run next to xemu.
"""

import argparse
import base64
import hashlib
import json
import re
import socket
import struct
import sys
import threading
import time
from xml.sax import saxutils

import app

WS_GUID = b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

# Event ids as named by the game: 0 smack, 1 race, 2 flag.
SMACK_EVENT = "0"

# The game's own arena table (CelText.txt inside smallFiles.dat in the ISO)
# lists twelve arenas, each with the two numbers the level packs carry as
# l0_ThemeNumber / l0_ArenaNumber: a theme (0 DS, 1 JG, 2 TR, 3 SP) and an
# arena number 1-3 within that theme, theme by theme:
#
#   dw_DS2_Arena 0 1   dw_DS1_Arena 0 2   cf_DS4_Arena 0 3
#   cf_JG1_arena 1 1   cf_JG2_Arena 1 2   cf_JG3_Arena 1 3
#   dw_TR2_Arena 2 1   cf_TR3_Arena 2 2   dw_TR1_Arena 2 3
#   dw_SP3_Arena 3 1   dw_SP2_arena 3 2   dw_SP1_Arena 3 3
#
# The arena names sit in the menu text file in that same order (twelve names,
# three per theme), which is the order the game indexes arenas in: save digit
# = theme * 3 + (arena number - 1).  The save only has room for one digit, so
# arena 9 (Space 1, Babbling 5) is the last stage that can be recorded at all.
# The game's twelve arenas, in the order the save counts them.
ARENA_THEMES = (
    ("Wild Wooly West", "Death Valley", "Mesa Madness"),            # DS 1-3
    ("Swamp Stomp", "Temple of Boom", "Shmoe vs. the Volcano"),      # JG 1-3
    ("Monster Mosh", "Count's Castle", "Bohemian Raspberry"),        # TR 1-3
    ("Babbling 5", "Lunarcy", "Shooting Stars"),                    # SP 1-3
)
ARENAS = tuple(name for theme in ARENA_THEMES for name in theme)

# A single-digit arena index is all the save has (arenaWonFlags<arena><event>,
# thirty fields for ten arenas x three events), so only the first ten arenas of
# the twelve the game ships can ever record a win.
WON_ARENA_SLOTS = 10

# One entry per split: the save field that records the win plus the arena name
# shown in the timer.  This is the only list of stages: the .lss names these
# entries directly, and the watcher's win filter comes from won_fields(), which
# takes its arena digits from here.  So a split can never name a stage the timer
# does not watch, or the reverse.
SMACK_STAGES = tuple(
    (app.WON_PREFIX + str(arena) + SMACK_EVENT, ARENAS[arena])
    for arena in range(WON_ARENA_SLOTS))
SMACK_ARENAS = tuple(name for _, name in SMACK_STAGES)
ARENA_COUNT = len(SMACK_STAGES)


def won_fields(event=SMACK_EVENT):
    """The save fields a win can appear in, taken from SMACK_STAGES.

    Same arena digits and same count as the splits file, so the watcher and the
    .lss cannot drift apart; only the event digit follows --event (0 smack).
    """
    return frozenset(field[:-1] + event for field, _ in SMACK_STAGES)


def arena_name(key):
    """Arena that a won flag belongs to, e.g. arenaWonFlags30 -> "Swamp Stomp"."""
    digit = key[len(app.WON_PREFIX)]
    if digit.isdigit() and int(digit) < len(ARENAS):
        return ARENAS[int(digit)]
    return key


def is_won_flag(key, event=SMACK_EVENT):
    """True for a win flag of the given event type (arenaWonFlags<arena><event>).

    Shape test only; the watcher also requires the field to be one of the
    won_fields() stages, so a win in an arena with no split cannot start one.
    """
    return (key.startswith(app.WON_PREFIX)
            and len(key) == len(app.WON_PREFIX) + 2
            and key[-1] == event)


class Profiles:
    """Reads the win flags out of the live save files on the HDD image.

    Each save slot is tracked separately: the game only maintains the slot in
    use and leaves the other one stale, so a slot is never compared with its
    neighbour.
    """

    def __init__(self, image=None, event=SMACK_EVENT):
        self.path = app.resolve_image(image)
        self.event = event
        self.fields = won_fields(event)
        self.img = app.open_image(self.path)
        self.offsets = self.find_offsets()
        self.misses = 0

    def find_offsets(self):
        """The game can move a save slot, so locate them fresh each launch."""
        saves = app.find_saves(self.img, app.progress_printer("scanning"))
        offsets = [s["text_offset"] for s in saves]
        if offsets:
            print("\r%d save(s) found" % len(offsets), flush=True)
            return offsets
        return [b["offset"] for b in app.load_baselines() if b.get("offset")]

    def read(self):
        """{save offset: set of won arena flags}, or None if unreadable."""
        states = {}
        for offset in self.offsets:
            save = app.try_save_at(self.img, offset)
            if not save:
                continue
            states[offset] = frozenset(
                key for key, value in save["fields"]
                if value and key in self.fields)
        if states:
            self.misses = 0
            return states
        self.misses += 1
        if self.misses >= 3:
            self.rescan()
        return None

    def rescan(self):
        """Look again, on a new handle: the save may have been relocated."""
        self.misses = 0
        self.img.close()
        self.img = app.open_image(self.path)
        offsets = self.find_offsets()
        if offsets and offsets != self.offsets:
            print("profile moved; watching %s"
                  % ", ".join("%#x" % o for o in offsets), flush=True)
            self.offsets = offsets


class Timer:
    def send(self, command):
        raise NotImplementedError


class Printer(Timer):
    def send(self, command):
        print("%s  -> %s" % (time.strftime("%H:%M:%S"), command), flush=True)


class TcpTimer(Timer):
    """LiveSplit's internal server (Control -> Start TCP Server)."""

    NAMES = {"splitOrStart": "startorsplit", "reset": "reset"}

    def __init__(self, host, port):
        self.host = host
        self.port = port
        self.sock = None
        self.warned = False

    def send(self, command):
        line = (self.NAMES[command] + "\n").encode()
        for attempt in (0, 1):
            try:
                if self.sock is None:
                    self.sock = socket.create_connection((self.host, self.port),
                                                         timeout=3)
                self.sock.sendall(line)
                self.warned = False
                return True
            except OSError as exc:
                self.sock = None
                if attempt and not self.warned:
                    print("livesplit: cannot reach %s:%s (%s)"
                          % (self.host, self.port, exc), flush=True)
                    self.warned = True
        return False


class WsTimer(Timer):
    """WebSocket server for LiveSplit One's "Connect to Server"."""

    def __init__(self, host, port):
        self.clients = []
        self.lock = threading.Lock()
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind((host, port))
        self.sock.listen(4)
        threading.Thread(target=self._accept, daemon=True).start()
        print("waiting for LiveSplit One on ws://%s:%d" % (host, port), flush=True)

    def _accept(self):
        while True:
            try:
                conn, _ = self.sock.accept()
            except OSError:
                return
            threading.Thread(target=self._serve, args=(conn,), daemon=True).start()

    def _serve(self, conn):
        try:
            request = b""
            while b"\r\n\r\n" not in request:
                chunk = conn.recv(4096)
                if not chunk:
                    conn.close()
                    return
                request += chunk
            key = re.search(rb"Sec-WebSocket-Key:\s*(\S+)", request)
            if not key:
                conn.close()
                return
            accept = base64.b64encode(
                hashlib.sha1(key.group(1) + WS_GUID).digest())
            conn.sendall(b"HTTP/1.1 101 Switching Protocols\r\n"
                         b"Upgrade: websocket\r\nConnection: Upgrade\r\n"
                         b"Sec-WebSocket-Accept: " + accept + b"\r\n\r\n")
            with self.lock:
                self.clients.append(conn)
            print("LiveSplit One connected", flush=True)
            self._drain(conn)
        except OSError:
            pass
        finally:
            with self.lock:
                if conn in self.clients:
                    self.clients.remove(conn)
            try:
                conn.close()
            except OSError:
                pass

    def _drain(self, conn):
        """Read frames until the timer disconnects (we only send commands)."""
        while True:
            head = conn.recv(2)
            if len(head) < 2:
                return
            opcode = head[0] & 0x0F
            length = head[1] & 0x7F
            if length == 126:
                length = struct.unpack(">H", conn.recv(2))[0]
            elif length == 127:
                length = struct.unpack(">Q", conn.recv(8))[0]
            mask = conn.recv(4) if head[1] & 0x80 else b""
            payload = b""
            while len(payload) < length:
                chunk = conn.recv(length - len(payload))
                if not chunk:
                    return
                payload += chunk
            if mask:
                payload = bytes(c ^ mask[i % 4] for i, c in enumerate(payload))
            if opcode == 0x8:  # close
                return
            if opcode == 0x9:  # ping
                self._frame(conn, payload, 0xA)

    @staticmethod
    def _frame(conn, payload, opcode=0x1):
        header = bytes([0x80 | opcode])
        n = len(payload)
        if n < 126:
            header += bytes([n])
        elif n < 65536:
            header += bytes([126]) + struct.pack(">H", n)
        else:
            header += bytes([127]) + struct.pack(">Q", n)
        conn.sendall(header + payload)

    def wait_for_client(self, timeout=5.0):
        end = time.time() + timeout
        while time.time() < end:
            with self.lock:
                if self.clients:
                    return True
            time.sleep(0.05)
        return False

    def send(self, command):
        message = json.dumps({"command": command}).encode()
        with self.lock:
            clients = list(self.clients)
        for conn in clients:
            try:
                self._frame(conn, message)
            except OSError:
                with self.lock:
                    if conn in self.clients:
                        self.clients.remove(conn)


def parse_host_port(value):
    host, _, port = value.rpartition(":")
    return (host or "127.0.0.1"), int(port)


def lss_xml():
    """A LiveSplit .lss with one split per smack stage, named after its arena."""
    segments = []
    for _, name in SMACK_STAGES:
        segments.append(
            "    <Segment>\n"
            "      <Name>%s</Name>\n"
            "      <Icon />\n"
            "      <SplitTimes>\n"
            "        <SplitTime name=\"Personal Best\" />\n"
            "      </SplitTimes>\n"
            "      <BestSegmentTime />\n"
            "      <SegmentHistory />\n"
            "    </Segment>\n" % saxutils.escape(name))
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<Run version="1.8.0">\n'
        "  <GameIcon />\n"
        "  <GameName>Cel Damage</GameName>\n"
        "  <CategoryName>All Smacks</CategoryName>\n"
        "  <Metadata>\n"
        '    <Run id="" />\n'
        '    <Platform usesEmulator="True">Xbox</Platform>\n'
        "    <Region />\n"
        "    <Variables />\n"
        "  </Metadata>\n"
        "  <Offset>00:00:00</Offset>\n"
        "  <AttemptCount>0</AttemptCount>\n"
        "  <AttemptHistory />\n"
        "  <Segments>\n" + "".join(segments) + "  </Segments>\n"
        "</Run>\n")


def describe(states):
    counts = ["%#x: %d/%d" % (offset, len(won), ARENA_COUNT)
              for offset, won in states.items()]
    return ", ".join(counts)


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Split a Cel Damage speedrun timer from the game's own "
                    "save progress.")
    parser.add_argument("--image", help="HDD image (default: xemu's)")
    parser.add_argument("--serve", type=int, metavar="PORT",
                        help="host a WebSocket server for LiveSplit One")
    parser.add_argument("--connect", metavar="HOST:PORT",
                        help="connect to LiveSplit's TCP server")
    parser.add_argument("--dry-run", action="store_true",
                        help="print the timer commands instead of sending them")
    parser.add_argument("--interval", type=float, default=1.0,
                        help="seconds between progress checks (default 1.0)")
    parser.add_argument("--event", default=SMACK_EVENT, metavar="N",
                        help="event digit that counts as a win: 0 smack, "
                             "1 race, 2 flag (default 0)")
    parser.add_argument("--demo", action="store_true",
                        help="send start, two splits and a reset, then exit")
    parser.add_argument("--lss", metavar="PATH",
                        help="write an All Smacks LiveSplit splits file and exit")
    args = parser.parse_args(argv)

    if args.lss:
        with open(args.lss, "w", encoding="utf-8") as f:
            f.write(lss_xml())
        print("wrote %s with %d splits: %s"
              % (args.lss, len(SMACK_ARENAS), ", ".join(SMACK_ARENAS)), flush=True)
        return 0

    if args.dry_run:
        timer = Printer()
    elif args.serve:
        timer = WsTimer("127.0.0.1", args.serve)
    elif args.connect:
        timer = TcpTimer(*parse_host_port(args.connect))
    else:
        parser.error("choose --dry-run, --serve PORT, or --connect HOST:PORT")
        return 2

    if args.demo:
        # Give a human time to connect LiveSplit One before the demo runs.
        if isinstance(timer, WsTimer) and not timer.wait_for_client():
            print("demo: nobody connected", flush=True)
        for command in ("splitOrStart", "splitOrStart", "splitOrStart", "reset"):
            timer.send(command)
            time.sleep(0.4)
        return 0

    profiles = Profiles(args.image, args.event)
    print("watching %s at %s for smack wins (arenaWonFlags<arena>%s)"
          % (profiles.path, ", ".join("%#x" % o for o in profiles.offsets),
             args.event), flush=True)

    known = None
    try:
        while True:
            states = profiles.read()
            if states is not None:
                if known is None or set(states) != set(known):
                    known = states
                    print("baseline wins: %s" % describe(states), flush=True)
                else:
                    fresh = [key for offset, won in states.items()
                             for key in won - known[offset]]
                    cleared = [key for offset, won in states.items()
                               for key in known[offset] - won]
                    if fresh:
                        print("%s  split: %s"
                              % (time.strftime("%H:%M:%S"),
                                 ", ".join("%s (%s)" % (k, arena_name(k))
                                           for k in sorted(fresh))),
                              flush=True)
                        timer.send("splitOrStart")
                    if cleared:
                        print("%s  progress cleared: %s"
                              % (time.strftime("%H:%M:%S"), ", ".join(sorted(cleared))),
                              flush=True)
                    known = states
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("stopped", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
