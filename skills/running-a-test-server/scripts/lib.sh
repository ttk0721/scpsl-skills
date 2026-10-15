#!/usr/bin/env bash
# Shared helpers for the running-a-test-server scripts. Source it, do not run it.
# Everything is derived from the environment; nothing is hardcoded to a user or a machine.
#
# These scripts are LINUX ONLY. They need mkfifo, setsid, /proc and GNU stat/ps, and they were only ever
# run on Linux. Git Bash, MSYS and Cygwin are NOT supported and were never tried: each script calls
# slt_require_linux and refuses elsewhere instead of half-working. Windows: manual procedure in
# launch-and-console.md (unverified).
#
# Overrides (all optional):
#   SLT_CONFIG_ROOT  the "SCP Secret Laboratory" app-data folder
#   SLT_INSTALL      the dedicated-server install folder (contains LocalAdmin)
#   SLT_RUN_DIR      where this tooling keeps its own state (fifo, log, pid, markers)
#   SLT_STOP_WAIT    seconds stop-server.sh waits for the graceful exit (default 40)
#   SLT_KILL_WAIT    seconds stop-server.sh --kill waits after each signal before it checks (default 10)
#   SLT_PROC_NET     where the listening-port fallback reads sockets from (default /proc/net; tests only)

slt_os() {
  case "$(uname -s 2>/dev/null)" in
    Linux*) echo linux ;;
    MINGW* | MSYS* | CYGWIN*) echo windows ;;
    *) echo other ;;
  esac
}

# Refuses (exit 69) anywhere but Linux. Called first by every script, so a wrong platform costs one
# clear message and no half-created fifo or state folder.
slt_require_linux() {
  if [ "$(slt_os)" != linux ]; then
    echo "These scripts are Linux-only (mkfifo, setsid, /proc, GNU stat/ps); this system reports '$(uname -s 2>/dev/null)'." >&2
    echo "Git Bash, MSYS and Cygwin are not supported and were never tried. Use the manual procedure in launch-and-console.md (Windows, unverified)." >&2
    exit 69
  fi
}

# Argument errors all end here: message, usage line, exit 64.
slt_usage() {
  echo "$1" >&2
  [ -n "${2:-}" ] && echo "usage: $2" >&2
  exit 64
}

# slt_need_value <flag> <args left including the flag> <next arg or ""> <usage>
# A flag that takes a value must be followed by one. "shift 2" with one argument left shifts nothing,
# which is how a trailing "--install" once made a parsing loop spin forever.
slt_need_value() {
  if [ "$2" -lt 2 ]; then
    slt_usage "$1 needs a value" "$4"
  fi
  case "$3" in
    '' | --*) slt_usage "$1 needs a value, got '$3'" "$4" ;;
  esac
}

slt_is_uint() {
  case "$1" in
    '' | *[!0-9]*) return 1 ;;
  esac
  return 0
}

# App-data root the game and LabAPI use (Environment.SpecialFolder.ApplicationData + "SCP Secret Laboratory").
slt_config_root() {
  if [ -n "${SLT_CONFIG_ROOT:-}" ]; then
    printf '%s\n' "$SLT_CONFIG_ROOT"
    return
  fi
  printf '%s/SCP Secret Laboratory\n' "${XDG_CONFIG_HOME:-$HOME/.config}"
}

# EXILED keeps its own root next to the game's app-data folder.
slt_exiled_root() {
  local root
  root="$(slt_config_root)"
  printf '%s/EXILED\n' "$(dirname "$root")"
}

# Folder holding this tooling's state for one port.
slt_state_dir() {
  local base="${SLT_RUN_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/scpsl-skills/test-server}"
  printf '%s/%s\n' "$base" "$1"
}

# Succeeds when "$1" is a plausible install folder.
slt_is_install() {
  [ -d "$1" ] && [ -f "$1/LocalAdmin" ] && [ -d "$1/SCPSL_Data" ]
}

# Prints every install folder it can find, one per line.
#
# The ONLY places looked at (the rule in running-a-test-server/paths.md, "Looking for the install"):
#   1. SLT_INSTALL, when the caller gave one
#   2. the default Steam client locations listed below
#   3. the library folders those clients name in steamapps/libraryfolders.vdf
# Nothing recursive: no find, locate or grep over the home folder or the disk.
slt_find_installs() {
  local name="SCP Secret Laboratory Dedicated Server" cand lib vdf
  local -a libs=()
  if [ -n "${SLT_INSTALL:-}" ]; then
    slt_is_install "$SLT_INSTALL" && printf '%s\n' "$SLT_INSTALL"
  fi
  # Flatpak path UNVERIFIED.
  for cand in \
    "${XDG_DATA_HOME:-$HOME/.local/share}/Steam" \
    "$HOME/.steam/steam" \
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
    [ -d "$cand/steamapps" ] && libs+=("$cand")
  done
  for lib in "${libs[@]}"; do
    vdf="$lib/steamapps/libraryfolders.vdf"
    if [ -f "$vdf" ]; then
      while IFS= read -r cand; do
        [ -n "$cand" ] && [ -d "$cand/steamapps" ] && libs+=("$cand")
      done < <(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf")
    fi
  done
  for lib in "${libs[@]}"; do
    cand="$lib/steamapps/common/$name"
    slt_is_install "$cand" && readlink -f "$cand"
  done | awk '!seen[$0]++'
}

# The install this tooling recorded when it first started <port>, or nothing (exit 1). Stored in the
# state folder so a later list-created.sh scans the install the server really ran from, not whichever one
# happens to be found first.
slt_recorded_install() {
  local f v
  f="$(slt_state_dir "$1")/install"
  [ -f "$f" ] || return 1
  IFS= read -r v <"$f" 2>/dev/null
  [ -n "$v" ] || return 1
  printf '%s\n' "$v"
}

# slt_pick_install <port> <explicit install or "">
# Decides WHICH install a script works on and sets SLT_PICK. Order, first hit wins:
#   1. the explicit value (an --install flag)
#   2. the install recorded in <port>'s state when it was started
#   3. SLT_INSTALL from the environment
#   4. the default Steam locations (slt_find_installs); exactly one hit is used, several are the owner's
#      choice and are refused, never settled by "the first one"
# Returns 0 and sets SLT_PICK; 1 when no install exists at all (no message, the caller decides whether
# that matters); 3 when an explicit or recorded install is not usable; 6 when several were found. Every
# non-zero case except 1 has printed its message to stderr.
slt_pick_install() {
  local port="$1" explicit="${2:-}" rec found
  SLT_PICK=""
  if [ -n "$explicit" ]; then
    if ! slt_is_install "$explicit"; then
      echo "'$explicit' is not an install folder (it needs LocalAdmin and SCPSL_Data)." >&2
      return 3
    fi
    SLT_PICK="$explicit"
    return 0
  fi
  if rec="$(slt_recorded_install "$port")"; then
    if ! slt_is_install "$rec"; then
      echo "The install recorded for port $port ($rec) is gone or no longer an install. Pass --install DIR, or delete $(slt_state_dir "$port")/install if it moved." >&2
      return 3
    fi
    SLT_PICK="$rec"
    return 0
  fi
  if [ -n "${SLT_INSTALL:-}" ]; then
    if ! slt_is_install "$SLT_INSTALL"; then
      echo "SLT_INSTALL='$SLT_INSTALL' is not an install folder (it needs LocalAdmin and SCPSL_Data)." >&2
      return 3
    fi
    SLT_PICK="$SLT_INSTALL"
    return 0
  fi
  found="$(slt_find_installs)"
  [ -n "$found" ] || return 1
  if [ "$(printf '%s\n' "$found" | grep -c .)" -gt 1 ]; then
    echo "More than one install found and none is recorded for port $port:" >&2
    printf '%s\n' "$found" >&2
    echo "ASK the user which one, then pass it with --install." >&2
    return 6
  fi
  SLT_PICK="$found"
}

slt_is_port() {
  case "$1" in
    '' | *[!0-9]*) return 1 ;;
  esac
  [ "$1" -ge 1024 ] && [ "$1" -le 65535 ]
}

# Prints the ports something is listening on right now, one per line, sorted. Exit 1 when NO method works:
# the caller must then treat every port as possibly busy (fail closed), never as free.
# SCP:SL listens on UDP, so UDP matters most. Method 1 is `ss`; method 2 is /proc/net, for machines
# without iproute2: udp/udp6 sockets in state 07 (bound, not connected: what `ss -l` shows as UNCONN) and
# tcp/tcp6 sockets in state 0A (LISTEN). The fallback needs udp or udp6 to be readable, otherwise it knows
# nothing about the one protocol that counts. SLT_PROC_NET overrides /proc/net (tests only).
slt_listening_ports() {
  local out net f want la st hex ok=0
  if command -v ss >/dev/null 2>&1 && out="$(ss -H -lntu 2>/dev/null)"; then
    printf '%s\n' "$out" | awk 'NF >= 5 { n = split($5, a, ":"); print a[n] }' | sort -un
    return 0
  fi
  net="${SLT_PROC_NET:-/proc/net}"
  for f in udp udp6; do
    [ -r "$net/$f" ] && ok=1
  done
  [ "$ok" -eq 1 ] || return 1
  for f in udp udp6 tcp tcp6; do
    [ -r "$net/$f" ] || continue
    case "$f" in udp*) want=07 ;; *) want=0A ;; esac
    # Columns: sl local_address rem_address st ... ; the first line is a header, which fails the hex test.
    while read -r _ la _ st _; do
      [ "$st" = "$want" ] || continue
      hex="${la##*:}"
      case "$hex" in '' | *[!0-9A-Fa-f]*) continue ;; esac
      echo $((16#$hex))
    done 2>/dev/null <"$net/$f"
  done | sort -un
}

# Prints the EULA acceptance timestamp LocalAdmin recorded, or nothing (exit 1). READ-ONLY: this file is
# the owner's and is never written. Observed on a real machine: config/localadmin_internal_data.json holds
# "EulaAccepted":"<ISO time>" after a server was run there. Not verified: whether the prompt, the
# --acceptEULA flag or both write it, and the value on a never-accepted install (assumed null, like its
# sibling fields). The record is machine-wide, not per install, so it never lets a caller pass
# --acceptEULA for the owner. All it decides is whether start-server.sh may start WITHOUT the flag, because
# LocalAdmin does not prompt when an acceptance is stored; with no record and no explicit --accept-eula
# start-server.sh refuses, since LocalAdmin would block on its interactive prompt.
slt_eula_record() {
  local f v
  f="$(slt_config_root)/config/localadmin_internal_data.json"
  [ -f "$f" ] || return 1
  v="$(sed -n 's/.*"EulaAccepted"[[:space:]]*:[[:space:]]*"\([^"]\{1,\}\)".*/\1/p' "$f" | head -n 1)"
  [ -n "$v" ] || return 1
  printf '%s\n' "$v"
}

# Process start time in epoch seconds, from /proc (field 22 of /proc/<pid>/stat is in clock ticks since boot).
slt_proc_start_epoch() {
  local stat rest btime hz
  local -a f=()
  stat="$(cat "/proc/$1/stat" 2>/dev/null)" || return 1
  rest="${stat##*) }" # skip "pid (comm) ": comm may hold spaces and parentheses
  read -r -a f <<<"$rest"
  [ -n "${f[19]:-}" ] || return 1
  btime="$(awk '/^btime/ { print $2 }' /proc/stat 2>/dev/null)"
  hz="$(getconf CLK_TCK 2>/dev/null || echo 100)"
  [ -n "$btime" ] && slt_is_uint "$btime" && slt_is_uint "${f[19]}" || return 1
  echo $((btime + f[19] / hz))
}

# slt_pid_is_ours <pid> <port> <pidfile>
# A pid file only says what pid WAS ours. Pids are reused, so before any signal or any write toward that
# process, prove it is the LocalAdmin this tooling started for this port:
#   - the pid is a plain number above 1
#   - /proc/<pid>/cmdline names a LocalAdmin launcher (argv[0], or argv[1] when a script is run through an
#     interpreter) and carries the port as an argument of its own
#   - the process started no later than the pid file was written (a reused pid starts AFTER it)
# On failure it sets SLT_WHY and returns 1. Never signal a process group whose leader did not pass this.
slt_pid_is_ours() {
  local pid="$1" port="$2" pidfile="$3" arg i started written
  local -a argv=()
  SLT_WHY=""
  if ! slt_is_uint "$pid" || [ "$pid" -le 1 ]; then
    SLT_WHY="the pid file does not hold a usable pid ('$pid')"
    return 1
  fi
  [ -r "/proc/$pid/cmdline" ] || {
    SLT_WHY="cannot read /proc/$pid/cmdline (the process is gone, or there is no /proc)"
    return 1
  }
  while IFS= read -r arg; do
    argv+=("$arg")
  done < <(tr '\0' '\n' <"/proc/$pid/cmdline" 2>/dev/null)
  if [ "${#argv[@]}" -lt 2 ]; then
    SLT_WHY="pid $pid has no command line to compare (a zombie, or not our process)"
    return 1
  fi
  if [ "${argv[0]##*/}" != LocalAdmin ] && [ "${argv[1]##*/}" != LocalAdmin ]; then
    SLT_WHY="pid $pid is '${argv[0]##*/}', not LocalAdmin: the pid was reused by another program"
    return 1
  fi
  i=1
  while [ "$i" -lt "${#argv[@]}" ]; do
    [ "${argv[$i]}" = "$port" ] && break
    i=$((i + 1))
  done
  if [ "$i" -ge "${#argv[@]}" ]; then
    SLT_WHY="pid $pid is a LocalAdmin, but not for port $port (another instance, probably the owner's)"
    return 1
  fi
  started="$(slt_proc_start_epoch "$pid")" || {
    SLT_WHY="cannot read the start time of pid $pid"
    return 1
  }
  written="$(stat -c %Y "$pidfile" 2>/dev/null)" || {
    SLT_WHY="cannot read the age of $pidfile"
    return 1
  }
  # 10 s of slack: the boot time /proc reports can shift a little when the clock is corrected.
  if [ "$started" -gt $((written + 10)) ]; then
    SLT_WHY="pid $pid started after the pid file was written: the pid was reused"
    return 1
  fi
  return 0
}

# Succeeds while <pid> is a live process. A zombie (dead, not yet reaped by its parent) answers `kill -0`
# but is gone for every practical purpose, so it counts as dead.
slt_pid_alive() {
  local stat rest
  kill -0 "$1" 2>/dev/null || return 1
  IFS= read -r stat 2>/dev/null <"/proc/$1/stat" || return 1
  rest="${stat##*) }" # skip "pid (comm) ": comm may hold spaces and parentheses
  [ "${rest%% *}" != Z ]
}

# Prints the live (non-zombie) pids whose process group is <pgid>. Reads /proc directly: `ps -g` selects by
# session or effective GROUP, not by process group, and would name unrelated processes.
slt_group_members() {
  local f stat pid
  local -a fld=()
  for f in /proc/[0-9]*/stat; do
    IFS= read -r stat 2>/dev/null <"$f" || continue
    read -r -a fld <<<"${stat##*) }" # state, ppid, pgrp, ...
    if [ "${fld[2]:-}" = "$1" ] && [ "${fld[0]:-}" != Z ]; then
      pid="${f#/proc/}"
      echo "${pid%%/*}"
    fi
  done
}

# Opens a FIFO for writing without ever blocking: O_RDWR on a FIFO does not wait for a reader on Linux, a
# plain "> fifo" does and would hang the script forever when the server is gone. The line stays in the
# pipe buffer if nobody reads it, which is harmless. Usage: slt_fifo_write <fifo> <line>
slt_fifo_write() {
  exec 9<>"$1" || return 1
  printf '%s\n' "$2" >&9
  exec 9>&-
}
