#!/usr/bin/env bash
# Usage: bash scripts/check-private.sh <port> [logfile]
# Decides whether a local instance is private (not on the public server list).
#   config: contact_email in config/<port>/config_gameplay.txt must be unset ("default" or empty)
#   log:    the game prints "Your server won't be visible on the public server list" when it is not listed
# Exit 0 = private (both agree), 1 = contact_email is set (treat as possibly public, stop and ask),
#      2 = not determined yet (config unset, but the log line has not been seen).
# Read-only.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"

slt_require_linux

usage="check-private.sh <port> [logfile]"
port="${1:-}"
slt_is_port "$port" || slt_usage "first argument must be a port number (1024-65535), got '$port'" "$usage"
[ $# -le 2 ] || slt_usage "too many arguments" "$usage"
root="$(slt_config_root)"
cfg="$root/config/$port/config_gameplay.txt"
log="${2:-$(slt_state_dir "$port")/server.log}"

if [ -f "$cfg" ]; then
  line="$(grep -m1 '^contact_email: ' "$cfg" || true)"
  value="${line#contact_email: }"
  value="${value%$'\r'}"          # CRLF files (Windows)
  value="${value#"${value%%[![:space:]]*}"}"   # trim leading blanks
  value="${value%"${value##*[![:space:]]}"}"   # trim trailing blanks
  if [ -n "$line" ] && [ "$value" != "default" ] && [ -n "$value" ]; then
    echo "contact_email is SET in $cfg: this instance may be listed publicly. Stop and ask the owner."
    exit 1
  fi
  echo "config: contact_email unset"
else
  echo "config: $cfg does not exist yet (the first start creates it from the template with contact_email unset)"
fi

if [ -f "$log" ] && grep -q "won't be visible on the public server list" "$log"; then
  echo "log: 'won't be visible on the public server list' seen: private"
  exit 0
fi
echo "log: the 'won't be visible' line is not in $log yet. Wait for boot, then run this again."
exit 2
