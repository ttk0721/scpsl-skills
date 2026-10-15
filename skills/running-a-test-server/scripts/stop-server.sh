#!/usr/bin/env bash
# Usage: bash scripts/stop-server.sh <port> [--kill [--sigkill]]
# Sends "exit" (graceful: players are notified, the game stops, LocalAdmin exits) and waits up to 40 s
# (SLT_STOP_WAIT overrides). --kill escalates to SIGTERM only if the graceful exit did not finish, waits
# SLT_KILL_WAIT seconds (default 10) and LOOKS whether the process is gone. --sigkill (only together with
# --kill) is a second, explicit step: SIGKILL when SIGTERM was not enough.
#
# "Gone" means the LocalAdmin pid is dead AND nobody is left in its process group (the game child). The pid
# file is removed only then. A signal that did not work is reported (exit 5) and the pid file stays, because
# it is the only handle on a server that is still up.
#
# The pid file is a claim, not proof: pids are reused. Before ANY write toward the process and before ANY
# signal, the pid is checked against /proc (lib.sh, slt_pid_is_ours): a LocalAdmin for this port that
# started no later than the pid file. Otherwise the script refuses (exit 6) and signals nothing, because
# SIGTERM to the group of a reused pid would hit whatever program owns it now.
# Exit codes: 64 bad arguments, 69 not Linux, 5 still running (also: the game child outlived LocalAdmin),
#             6 refused (pid not verified as ours).
# The install this port runs from is in the state (install file, see start-server.sh); stopping needs only the
# pid, but the next step, list-created.sh, uses it, so a successful stop names it.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
slt_require_linux

usage="stop-server.sh <port> [--kill [--sigkill]]"
port="${1:-}"
slt_is_port "$port" || slt_usage "first argument must be a port number (1024-65535), got '$port'" "$usage"
shift
kill_flag=0
sigkill_flag=0
while [ $# -gt 0 ]; do
  case "$1" in
    --kill)
      kill_flag=1
      shift
      ;;
    --sigkill)
      sigkill_flag=1
      shift
      ;;
    *) slt_usage "unknown argument: $1" "$usage" ;;
  esac
done
[ "$sigkill_flag" -eq 0 ] || [ "$kill_flag" -eq 1 ] || slt_usage "--sigkill only makes sense with --kill (SIGKILL is the second step after SIGTERM)" "$usage"

wait_s="${SLT_STOP_WAIT:-40}"
slt_is_uint "$wait_s" || slt_usage "SLT_STOP_WAIT must be a number of seconds, got '$wait_s'" "$usage"
kill_wait="${SLT_KILL_WAIT:-10}"
slt_is_uint "$kill_wait" || slt_usage "SLT_KILL_WAIT must be a number of seconds, got '$kill_wait'" "$usage"
state="$(slt_state_dir "$port")"
fifo="$state/console.fifo"
pidfile="$state/server.pid"

# Removes the handles and says what to do next. Called only when the process is really gone.
finish() {
  rm -f "$fifo" "$pidfile"
  local rec
  if rec="$(slt_recorded_install "$port")"; then
    echo "next: bash scripts/list-created.sh $port (scans the install recorded for this port: $rec)"
  fi
  exit 0
}

pid="$(cat "$pidfile" 2>/dev/null)" || {
  echo "not running"
  rm -f "$fifo"
  exit 0
}

# The pid file names the LocalAdmin; its game child shares the process group. A "dead leader" with live
# members is NOT "not running": the server is half up and this must say so.
if slt_is_uint "$pid" && [ "$pid" -gt 1 ] && ! slt_pid_alive "$pid"; then
  left="$(slt_group_members "$pid" | xargs)"
  if [ -n "$left" ]; then
    echo "LocalAdmin (pid $pid) is gone, but processes of its process group are still running: $left." >&2
    echo "Nothing was signalled: with the leader gone there is nothing left to prove they are ours. Look at them (ps -o pid,args -p ${left// /,}) and stop them by hand. The pid file stays." >&2
    exit 5
  fi
  echo "not running"
  rm -f "$fifo" "$pidfile"
  exit 0
fi
if ! slt_pid_is_ours "$pid" "$port" "$pidfile"; then
  echo "Refusing to touch pid '$pid': $SLT_WHY." >&2
  echo "Nothing was signalled and nothing was written to the console. If the test server is really gone, delete $pidfile by hand; if it is still up, find its LocalAdmin yourself (never pkill by name: that also hits the owner's servers)." >&2
  exit 6
fi

# start-server.sh runs LocalAdmin under setsid, so its pid is also its process group: a signal to the group
# takes the game child with it. Only a group whose leader is the verified pid is ever signalled as a group.
# Decided now, while the leader is alive and verified.
led_group=0
[ "$(ps -o pgid= -p "$pid" 2>/dev/null | tr -d ' ')" = "$pid" ] && led_group=1

# Succeeds when the process is gone: the leader is dead and, if it led a group, nobody is left in it.
# While members live the group id cannot be reused, so this cannot mistake a stranger for our leftovers.
gone() {
  slt_pid_alive "$pid" && return 1
  [ "$led_group" -eq 0 ] && return 0
  [ -z "$(slt_group_members "$pid")" ]
}

# wait_for_exit <seconds>: looks right away, then once a second; sets waited.
waited=0
wait_for_exit() {
  waited=0
  while :; do
    gone && return 0
    [ "$waited" -ge "$1" ] && return 1
    sleep 1
    waited=$((waited + 1))
  done
}

# What is still running, for a message.
still_up() {
  local m
  m="$(slt_group_members "$pid" | xargs)"
  if slt_pid_alive "$pid"; then
    echo "LocalAdmin pid $pid${m:+, group members: $m}"
  else
    echo "LocalAdmin is gone but its group still has: $m"
  fi
}

[ -p "$fifo" ] && slt_fifo_write "$fifo" exit

if wait_for_exit "$wait_s"; then
  echo "stopped after ~${waited}s"
  finish
fi

if [ "$kill_flag" -ne 1 ]; then
  echo "still running after ${wait_s} s ($(still_up)). Read the log; pass --kill to send SIGTERM to pid $pid." >&2
  exit 5
fi

# send <TERM|KILL>: look again right before signalling, because the waits above are long enough for the pid
# to change hands. With the leader dead only a group we verified earlier in THIS run may be signalled.
send() {
  if slt_pid_alive "$pid" && ! slt_pid_is_ours "$pid" "$port" "$pidfile"; then
    echo "Refusing to signal pid '$pid': $SLT_WHY." >&2
    exit 6
  fi
  if [ "$led_group" -eq 1 ]; then
    kill "-$1" -- "-$pid" 2>/dev/null
    echo "sent SIG$1 to process group $pid (LocalAdmin and its game child)"
  else
    kill "-$1" "$pid" 2>/dev/null
    echo "sent SIG$1 to pid $pid only. The game child may remain: check by hand, do not pkill by name"
  fi
}

echo "graceful exit timed out."
send TERM
if wait_for_exit "$kill_wait"; then
  echo "stopped after SIGTERM (~${waited}s)"
  finish
fi

if [ "$sigkill_flag" -ne 1 ]; then
  echo "still running ${kill_wait} s after SIGTERM ($(still_up)). The pid file stays: it is the only handle on a live server." >&2
  echo "Read the log. To escalate, run again with --kill --sigkill; this sends SIGKILL to the same verified target." >&2
  exit 5
fi

send KILL
if wait_for_exit "$kill_wait"; then
  echo "stopped after SIGKILL (~${waited}s)"
  finish
fi
echo "still running ${kill_wait} s after SIGKILL ($(still_up)). The pid file stays. SIGKILL cannot be caught, so the process is stuck in the kernel or owned by someone else: look at it by hand." >&2
exit 5
