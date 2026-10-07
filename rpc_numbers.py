"""
Works out the game's "message numbers" from an Il2CppDumper dump.cs.

WHY: the crawler reads who is on which team, the round score and the end of a match from the game's own messages.
The game sends each message as a short number, which is the message's place in an alphabetical list of the game's
multiplayer functions. When a game update adds or removes such a function, the numbers shift and the crawler would
silently stop seeing them. Run this on the dump.cs of the new game version to get the new numbers.

HOW:
  1. Dump the new game version with Il2CppDumper (the same way as before). You get a dump.cs.
  2. Run:        python rpc_numbers.py path\\to\\dump.cs
  3. Copy the block it prints into config.json, inside the "Crawl" section.

Checked against game version 1.07sp2: it gives BlueTeam 23, RedTeam 24, RoundScore 27, SwapSides 213,
MatchResult 199, NextMapVote 243, which are also the crawler's built-in numbers.
"""
import json
import re
import sys

# crawler setting name -> the game's function name
WANTED = {
    "BlueTeam": "AssignToBlueTeam",
    "RedTeam": "AssignToRedTeam",
    "RoundScore": "AssignWin_Global",
    "SwapSides": "SwitchTeamCards",
    "MatchResult": "ShowMatchResult",
    "NextMapVote": "VoteNextMatchMap",
}


def find_rpc_names(path):
    """Every function marked [PunRPC] in the dump, with the class it belongs to."""
    cls = None
    pending = False
    found = []
    class_re = re.compile(r"^(?:public|internal|private|protected)?\s*(?:static |sealed |abstract |partial )*(?:class|struct) ([^\s:<]+)")
    method_re = re.compile(r"^(?:public|private|internal|protected)\s+(?:static\s+)?(?:override\s+)?\S+\s+([^\s(]+)\(")
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = class_re.match(line)
            if m and "TypeDefIndex" in line:
                cls, pending = m.group(1), False
                continue
            s = line.strip()
            if s.startswith("[PunRPC]"):
                pending = True
                continue
            if pending:
                if s.startswith("//") or s.startswith("["):
                    continue
                mm = method_re.match(s)
                if mm:
                    found.append((cls, mm.group(1)))
                pending = False
    return found


def main():
    if len(sys.argv) < 2:
        sys.exit("Usage: python rpc_numbers.py path\\to\\dump.cs")
    found = find_rpc_names(sys.argv[1])
    if not found:
        sys.exit("No [PunRPC] functions found. Is this the right dump.cs?")
    # the list is sorted without regard to capital letters; the same name in two classes counts once
    order = sorted({n for _, n in found}, key=lambda s: (s.lower(), s))
    index = {n: i for i, n in enumerate(order)}
    print(f"Found {len(found)} functions ({len(order)} different names).\n")

    numbers, missing = {}, []
    for setting, name in WANTED.items():
        if name in index:
            numbers[setting] = index[name]
        else:
            missing.append(name)
    for setting, name in WANTED.items():
        if setting in numbers:
            owners = sorted({c for c, n in found if n == name})
            print(f"  {setting:12s} = {numbers[setting]:3d}   ({name}, in {', '.join(owners)})")
    if missing:
        print("\nNOT FOUND (the game may have renamed these): " + ", ".join(missing))
        print("The crawler keeps its built-in number for any setting you leave out.\n")

    print('\nPut this inside the "Crawl" section of config.json:\n')
    print('"RpcNumbers": ' + json.dumps(numbers, indent=2))


if __name__ == "__main__":
    main()
