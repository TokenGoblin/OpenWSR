#!/bin/sh
#
# Block personal information from entering a public repository.
#
# The values this looks for are NOT in this file, and must never be added to it: the script is
# committed, so a denylist written here would publish exactly what it is meant to protect. They
# are derived at run time from the machine instead —
#
#   - the OS username, from the environment
#   - the saved places and contact address, from %LOCALAPPDATA%\OpenWSR\settings.json
#   - the global git identity, when it differs from the one this repo commits under
#   - anything else, from .git/privacy-extra (one pattern per line; inside .git, so it can
#     never be committed — put a real name, a private hostname, an employer there)
#
# Usage:
#   tools/check-privacy.sh            what is staged        (what the pre-commit hook runs)
#   tools/check-privacy.sh --tree     every tracked file
#   tools/check-privacy.sh --history  every commit, for an audit before publishing anywhere new
#
# Exit 0 clean, 1 dirty. `git commit --no-verify` bypasses it deliberately.

set -e
mode="${1:---staged}"
root=$(git rev-parse --show-toplevel)
patterns=$(mktemp)
labels=$(mktemp)
trap 'rm -f "$patterns" "$labels"' EXIT

python - "$patterns" "$labels" "$root" <<'PYEOF'
import io, json, os, re, subprocess, sys

patterns_path, labels_path, root = sys.argv[1], sys.argv[2], sys.argv[3]
rules = []          # (regex, label) - the label is what gets printed, never the value

def add(value, label, minimum=4):
    if value and len(str(value).strip()) >= minimum:
        rules.append((re.escape(str(value).strip()), label))

# The account this machine logs in as. It appears in absolute paths, which is how it escapes.
for var in ("USERNAME", "USER", "LOGNAME"):
    add(os.environ.get(var), "the OS username")

# Saved places: the coordinates, at every precision they get printed at, and any name that is
# not the generic default.
local = os.environ.get("LOCALAPPDATA") or ""
settings = os.path.join(local, "OpenWSR", "settings.json")
if os.path.isfile(settings):
    try:
        data = json.load(io.open(settings, encoding="utf-8-sig"))
    except Exception:
        data = {}

    def coords(value, label):
        try:
            f = float(value)
        except (TypeError, ValueError):
            return
        # 2 to 4 decimals covers every way the app formats one, and the sign is part of it.
        for places in (2, 3, 4):
            rules.append((re.escape(f"{f:.{places}f}"), label))

    for place in (data.get("locations") or []):
        coords(place.get("latDeg"), "a saved place's latitude")
        coords(place.get("lonDeg"), "a saved place's longitude")
        name = (place.get("name") or "").strip()
        if name.lower() not in ("", "home", "work", "office", "map centre", "map center"):
            add(name, "a saved place's name", minimum=3)

    for legacy, label in (("homeLatDeg", "the legacy home latitude"),
                          ("homeLonDeg", "the legacy home longitude")):
        coords(data.get(legacy), label)

    add(data.get("contact"), "the contact address from settings")
    add(data.get("Contact"), "the contact address from settings")
    add(data.get("weatherUndergroundKey"), "a Weather Underground key", minimum=8)
    add(data.get("ambientApiKey"), "an Ambient Weather API key", minimum=8)
    add(data.get("ambientApplicationKey"), "an Ambient Weather application key", minimum=8)
    add(data.get("mapTilerKey"), "a MapTiler key", minimum=8)

# A global git identity that is not the handle this repository commits under.
def git(*args):
    try:
        return subprocess.run(["git", *args], cwd=root, capture_output=True,
                              text=True, check=False).stdout.strip()
    except Exception:
        return ""

repo_name, repo_email = git("config", "user.name"), git("config", "user.email")
for scope in ("--global", "--system"):
    for key, label in (("user.name", "a git identity other than this repo's"),
                       ("user.email", "a git email other than this repo's")):
        value = git("config", scope, key)
        if value and value not in (repo_name, repo_email):
            add(value, label)

# Anything the owner adds by hand. Inside .git, so it is never committed.
extra = os.path.join(root, ".git", "privacy-extra")
if os.path.isfile(extra):
    for line in io.open(extra, encoding="utf-8"):
        line = line.strip()
        if line and not line.startswith("#"):
            add(line, "a pattern from .git/privacy-extra", minimum=3)

# Credentials, by shape rather than by value. POSIX ERE only — git grep -E rejects an inline
# (?i) or a \b outright, and a rejected pattern file aborts the whole scan.
rules.append((r"""api[_-]?key[ 	]*[=:][ 	]*['"][A-Za-z0-9]{16,}""",
              "something shaped like an API key"))

seen, out_p, out_l = set(), [], []
for pattern, label in rules:
    if pattern in seen:
        continue
    seen.add(pattern)
    out_p.append(pattern)
    out_l.append(label)

io.open(patterns_path, "w", encoding="utf-8", newline="\n").write("\n".join(out_p) + "\n")
io.open(labels_path, "w", encoding="utf-8", newline="\n").write("\n".join(out_l) + "\n")
print(f"privacy check: {len(out_p)} pattern(s) built from this machine", file=sys.stderr)
PYEOF

set +e
case "$mode" in
  --tree)    subject=$(git grep -inE -f "$patterns" -- .) ;;
  --history) subject=$(git grep -inE -f "$patterns" $(git rev-list --all) -- .) ;;
  *)         # Added lines only. Existing content is not this commit's problem, and a file
             # touched for an unrelated reason must not block on a line it did not add.
             subject=$(git diff --cached -U0 | grep -E '^\+' | grep -inE -f "$patterns") ;;
esac
status=$?
set -e

# 0 matched, 1 matched nothing, anything above that is the search itself failing — and that
# has to stop the commit. An unreadable pattern file once aborted the scan and the script
# still printed "clean", which is the single outcome a guard must never produce.
if [ "$status" -gt 1 ]; then
  echo "privacy check: FAILED TO RUN (exit $status) — refusing to report clean" >&2
  exit 2
fi

if [ -z "$subject" ]; then
  echo "privacy check: clean"
  exit 0
fi

echo
echo "================================================================"
echo " BLOCKED: personal information in what you are about to commit"
echo "================================================================"
echo
echo "$subject" | head -40
echo
echo "This repository is public and pseudonymous. See 'Never leak personal"
echo "information' in CLAUDE.md. Location fixtures use Norman, Oklahoma."
echo
echo "If a match is a false positive, commit with --no-verify and say so."
echo
exit 1
