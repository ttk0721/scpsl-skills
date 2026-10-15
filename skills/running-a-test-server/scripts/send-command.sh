#!/usr/bin/env bash
# Usage: bash scripts/send-command.sh <port> <command...>
# Types one line into the test server's console and prints what the server logged in response.
#   plugin / LocalAdmin commands:  slprobe status
#   Remote Admin commands need a leading slash:  /dummy spawn Bot1   /forcestart (lobby only)
# SLT_WAIT sets how many seconds to wait for the answer (default 2).
# Linux only. The pid is verified as our LocalAdmin before anything is written (lib.sh), and the FIFO is
# opened read-write so a vanished server can never leave this script blocked on the open.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
slt_require_linux

usage="send-command.sh <port> <command...>"
port="${1:-}"
slt_is_port "$port" || slt_usage "first argument must be a port number (1024-65535), got '$port'" "$usage"
[ $# -ge 2 ] || slt_usage "missing command" "$usage"
shift
wait_s="${SLT_WAIT:-2}"
slt_is_uint "$wait_s" || slt_usage "SLT_WAIT must be a number of seconds, got '$wait_s'" "$usage"
state="$(slt_state_dir "$port")"
fifo="$state/console.fifo"
log="$state/server.log"
pidfile="$state/server.pid"

pid="$(cat "$pidfile" 2>/dev/null)"
if [ ! -p "$fifo" ] || ! slt_is_uint "$pid" || ! kill -0 "$pid" 2>/dev/null || ! slt_pid_is_ours "$pid" "$port" "$pidfile"; then
  echo "no running test server for port $port (start it with start-server.sh)${SLT_WHY:+: $SLT_WHY}" >&2
  exit 3
fi

before="$(wc -l <"$log")"
slt_fifo_write "$fifo" "$*"
sleep "$wait_s"
tail -n +"$((before + 1))" "$log"
