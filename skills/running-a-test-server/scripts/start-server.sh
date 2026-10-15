#!/usr/bin/env bash
# Usage: bash scripts/start-server.sh <port> [--install DIR] [--wait SECONDS] [--allow-existing-port] [--accept-eula]
# Starts LocalAdmin for <port> without a terminal and keeps its console reachable through a FIFO.
# Linux only (mkfifo, setsid, /proc). On Windows see launch-and-console.md (manual, UNVERIFIED).
#
# Safety:
#  - refuses a port that already has traces (config/<port>, LabAPI/<port> folders, logs, EXILED files,
#    a listening socket) unless this tooling created it earlier. --allow-existing-port is only for the case
#    where the OWNER explicitly asked to test on that port.
#  - never passes --acceptEULA on its own. Only --accept-eula does, and the agent uses it ONLY after the
#    owner said, in this conversation, that they accept it. Without the flag the script starts only when
#    LocalAdmin already recorded an acceptance on this machine (read-only look at
#    config/localadmin_internal_data.json, see lib.sh): then LocalAdmin does not prompt and needs no flag.
#    No record and no flag: it refuses with exit 7, because LocalAdmin would block on its interactive prompt.
#  - a port something is listening on RIGHT NOW is refused every time, also one this tooling created before
#    and also with --allow-existing-port; so is a port that cannot be checked (no ss, no /proc/net).
#  - the install it used is recorded in the state (install file). A restart reuses it without asking, and
#    list-created.sh scans it. Another install for the same port is refused until the first is cleaned up.
#
# Exit codes: 64 bad arguments, 69 not Linux, 3 no install, 2 owner's port or port in use or unverifiable,
#             7 EULA not accepted, 4 boot failed or timed out, 6 no usable install choice (several found,
#             or a different one than the recorded) or a pid file in the way.
#
# State (fifo, server.log, pid, install, port, created-epoch, started-epoch) lives in the state dir, see lib.sh.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
slt_require_linux

usage="start-server.sh <port> [--install DIR] [--wait SECONDS] [--allow-existing-port] [--accept-eula]"
port="${1:-}"
slt_is_port "$port" || slt_usage "first argument must be a port number (1024-65535), got '$port'" "$usage"
shift
install="${SLT_INSTALL:-}"
wait_s=90
allow_existing=0
accept_eula=0
while [ $# -gt 0 ]; do
  case "$1" in
    --install)
      slt_need_value "$1" $# "${2-}" "$usage"
      install="$2"
      shift 2
      ;;
    --wait)
      slt_need_value "$1" $# "${2-}" "$usage"
      slt_is_uint "$2" || slt_usage "--wait needs a number of seconds, got '$2'" "$usage"
      wait_s="$2"
      shift 2
      ;;
    --allow-existing-port)
      allow_existing=1
      shift
      ;;
    --accept-eula)
      accept_eula=1
      shift
      ;;
    *) slt_usage "unknown argument: $1" "$usage" ;;
  esac
done

state="$(slt_state_dir "$port")"

# Which install: --install or SLT_INSTALL, else the one recorded for this port, else the default Steam
# locations and libraryfolders.vdf (lib.sh, slt_pick_install). More than one hit is the owner's choice.
slt_pick_install "$port" "$install"
rc=$?
case "$rc" in
  0) install="$SLT_PICK" ;;
  1)
    echo "No usable install folder. ASK the user where the dedicated server is (the folder with LocalAdmin)." >&2
    exit 3
    ;;
  *) exit "$rc" ;;
esac
install="$(readlink -f "$install" 2>/dev/null || printf '%s' "$install")"
# A port that already ran from another install: its files would land where list-created.sh no longer looks.
if recorded_install="$(slt_recorded_install "$port")" && slt_is_install "$recorded_install" \
  && [ "$(readlink -f "$recorded_install")" != "$install" ]; then
  echo "Port $port was started from the install recorded in $state/install:" >&2
  echo "  recorded: $recorded_install" >&2
  echo "  asked:    $install" >&2
  echo "Use the recorded one (omit --install), or finish with it first: stop-server.sh, list-created.sh, clean up, then delete $state." >&2
  exit 6
fi
fifo="$state/console.fifo"
log="$state/server.log"
pidfile="$state/server.pid"

if [ -f "$pidfile" ]; then
  oldpid="$(cat "$pidfile" 2>/dev/null)"
  if slt_is_uint "$oldpid" && kill -0 "$oldpid" 2>/dev/null; then
    if slt_pid_is_ours "$oldpid" "$port" "$pidfile"; then
      echo "already running (pid $oldpid), log: $log"
      exit 0
    fi
    # A live pid that is not our LocalAdmin: the old server is gone and the number was reused.
    echo "ignoring a stale pid file: $SLT_WHY" >&2
  fi
fi

ours=0
[ -f "$state/created-epoch" ] && ours=1
if [ "$ours" -eq 0 ]; then
  # 0 = traces or listening, 1 = free, 3 = cannot tell. Only 1 means free.
  bash "$here/list-ports.sh" --has "$port"
  rc=$?
  if [ "$rc" -eq 3 ]; then
    echo "Refusing: the port could not be checked, so it is not known to be free." >&2
    exit 2
  elif [ "$rc" -ne 1 ]; then
    if [ "$allow_existing" -ne 1 ]; then
      echo "Port $port already has traces or is listening: it is the owner's. Refusing." >&2
      echo "Pick a free port (bash scripts/list-ports.sh --suggest) or, only if the owner asked, pass --allow-existing-port." >&2
      exit 2
    fi
    echo "WARNING: starting on an existing port at the owner's request."
    owner_port=1
  fi
fi

# EULA: decided before anything is created, so a refusal leaves no trace.
eula_args=()
if [ "$accept_eula" -eq 1 ]; then
  echo "EULA: accepting at the owner's explicit request (--accept-eula)."
  eula_args=(--acceptEULA)
elif recorded="$(slt_eula_record)"; then
  # The flag is NOT passed: with a stored acceptance LocalAdmin does not prompt, so it is not needed,
  # and the record is machine-wide, so it says nothing about this install or this owner.
  echo "EULA: LocalAdmin recorded an acceptance ($recorded) on this machine; not passing --acceptEULA, LocalAdmin will not prompt."
else
  echo "Refusing: no recorded EULA acceptance on this machine (config/localadmin_internal_data.json)." >&2
  echo "ASK the owner to accept the game's EULA themselves, or to tell you they accept it. Pass --accept-eula only after that." >&2
  exit 7
fi

# Right before the launch, for EVERY port, ours or not: a created-epoch file only says this tooling used the
# port once, it does not say nothing is listening there now (the owner may have started a server on it since,
# and an --allow-existing-port start is about idle traces, never about a live listener). Not overridable.
# 0 = something is listening, 1 = nothing is, 3 = cannot tell: only 1 lets the start go on.
bash "$here/list-ports.sh" --listening "$port"
rc=$?
if [ "$rc" -eq 0 ]; then
  echo "Port $port is listening right now: something is using it. Refusing to start a second server there." >&2
  echo "Pick a free port (bash scripts/list-ports.sh --suggest)." >&2
  exit 2
elif [ "$rc" -ne 1 ]; then
  echo "Refusing: the port could not be checked, so it is not known to be free." >&2
  exit 2
fi

mkdir -p "$state"
# Which install this port runs from, for list-created.sh (and so that a restart does not ask again).
printf '%s\n' "$install" >"$state/install"
printf '%s\n' "$port" >"$state/port"
# Remember that this port was the owner's, so list-created.sh never calls its files OURS.
[ "${owner_port:-0}" -eq 1 ] && date +%s >"$state/owner-port"
[ -f "$state/created-epoch" ] || date +%s >"$state/created-epoch"
date +%s >"$state/started-epoch"
rm -f "$fifo"
mkfifo "$fifo"
: >"$log"

cd "$install" || exit 1
# Open the FIFO read-write so single "echo cmd > fifo" writes never deliver EOF to the server.
exec 3<>"$fifo"
flags=("${eula_args[@]}" --useDefault --noSetCursor --noTerminalTitle --disableTrueColor)
if command -v setsid >/dev/null 2>&1; then
  setsid ./LocalAdmin "$port" "${flags[@]}" <&3 3<&- >>"$log" 2>&1 &
else
  nohup ./LocalAdmin "$port" "${flags[@]}" <&3 3<&- >>"$log" 2>&1 &
fi
echo $! >"$pidfile"
echo "started pid $(cat "$pidfile") on port $port, log: $log"

i=0
while [ "$i" -lt "$wait_s" ]; do
  if grep -q "Waiting for players" "$log" 2>/dev/null; then
    echo "READY after ~${i}s ('Waiting for players' seen)"
    exit 0
  fi
  if ! kill -0 "$(cat "$pidfile")" 2>/dev/null; then
    echo "process exited during boot, last log lines:" >&2
    tail -n 20 "$log" >&2
    exit 4
  fi
  sleep 1
  i=$((i + 1))
done
echo "not ready after ${wait_s}s; the server may still be booting. Read $log" >&2
exit 4
