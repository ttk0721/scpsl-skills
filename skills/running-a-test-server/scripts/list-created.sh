#!/usr/bin/env bash
# Usage: bash scripts/list-created.sh <port> [--since EPOCH] [--install DIR]
# Lists files and folders BORN (creation time, not modification time) after the test instance was first
# started, under the config root and the EXILED root. LabAPI and EXILED rewrite their configs on every
# load, so modification time cannot tell what a test created; birth time can.
# It NEVER deletes. Each line is classified:
#   OURS    path carries the test port: safe to remove once the test is over
#   SHARED  under a "global" folder: other ports may use it, decide with the owner
#   KEEP    "Round Logs Archive" zips: they hold old logs of the owner, never remove
#   OWNER   as OURS, but the port was the owner's (--allow-existing-port): ask before removing anything
#   OTHER   anything else born in the window (the owner may have worked meanwhile, or a plugin wrote
#           next to the game in the install folder): leave it
# Scans the config root, the EXILED root and the install folder. The install is the one start-server.sh
# recorded for this port (state/install), or --install DIR. With no record and several installs on the
# machine it refuses (exit 6) and asks, rather than scanning whichever came first.
# Needs GNU stat (%W, Linux) and a filesystem that records birth time. (find -newerBt is not used: some
# findutils builds refuse it.) Whole-second resolution.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"

slt_require_linux

usage="list-created.sh <port> [--since EPOCH] [--install DIR]"
port="${1:-}"
slt_is_port "$port" || slt_usage "first argument must be a port number (1024-65535), got '$port'" "$usage"
shift
since=""
install_arg=""
while [ $# -gt 0 ]; do
  case "$1" in
    --install)
      slt_need_value "$1" $# "${2-}" "$usage"
      install_arg="$2"
      shift 2
      ;;
    --since)
      slt_need_value "$1" $# "${2-}" "$usage"
      slt_is_uint "$2" || slt_usage "--since needs an epoch in seconds, got '$2'" "$usage"
      since="$2"
      shift 2
      ;;
    *) slt_usage "unknown argument: $1" "$usage" ;;
  esac
done
if [ -z "$since" ]; then
  marker="$(slt_state_dir "$port")/created-epoch"
  [ -f "$marker" ] || { echo "no created-epoch marker for port $port; pass --since <epoch>" >&2; exit 3; }
  since="$(cat "$marker")"
fi

ownerport=0
if [ -f "$(slt_state_dir "$port")/owner-port" ]; then
  ownerport=1
  echo "NOTE: port $port was the OWNER's (started with --allow-existing-port). Nothing below is tagged OURS: ask before removing any of it." >&2
fi

# Also the install folder: plugins that ran on the test port may write data next to the game (cwd).
# It is the install start-server.sh recorded for this port (or --install); never "the first one found".
# With none recorded and several on the machine it refuses (exit 6) instead of scanning a guess.
slt_pick_install "$port" "$install_arg"
rc=$?
case "$rc" in
  0 | 1) install="$SLT_PICK" ;; # 1: no install anywhere, nothing next to the game to scan
  *) exit "$rc" ;;
esac
roots=("$(slt_config_root)" "$(slt_exiled_root)")
cfgroot="${roots[0]}"
# Skip it when the config root already lives inside the install (hoster policy): it was scanned above.
if [ -n "$install" ] && [ -d "$install" ]; then
  case "$cfgroot/" in "$install"/*) ;; *) roots+=("$install") ;; esac
fi

for root in "${roots[@]}"; do
  [ -d "$root" ] || continue
  # "%W" is the birth time in epoch seconds, 0 when the filesystem does not record it.
  find "$root" -exec stat -c '%W|%n' {} + 2>/dev/null \
    | awk -F'|' -v s="$since" '$1 == 0 { unknown = 1 } $1 + 0 >= s { sub(/^[^|]*\|/, ""); print }
        END { if (unknown) print "NOBIRTH birth time is not recorded for some paths here" > "/dev/stderr" }' \
    | sort | while IFS= read -r path; do
    case "$path" in
      *"Round Logs Archive"*) tag=KEEP ;;
      */global/* | */global) tag=SHARED ;;
      */"$port" | */"$port"/* | */"$port".yml | */"$port"-* | */LabApi-"$port".yml)
        if [ "$ownerport" -eq 1 ]; then tag=OWNER; else tag=OURS; fi
        ;;
      *) tag=OTHER ;;
    esac
    printf '%-6s %s\n' "$tag" "$path"
  done
done
