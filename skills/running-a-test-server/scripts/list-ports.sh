#!/usr/bin/env bash
# Usage:
#   bash scripts/list-ports.sh              list every port that already has traces (the owner's ports)
#   bash scripts/list-ports.sh --suggest    print the first unused port, counting down from 9999
#   bash scripts/list-ports.sh --has <port> exit 0 if the port has traces or is listening, else 1
#   bash scripts/list-ports.sh --listening <port>  exit 0 if something is listening on it now (traces ignored), else 1
# Exit 3 from --has, --listening and --suggest: nothing on this machine can say what is listening (no ss, no
# readable /proc/net/udp). That is NOT "free": a caller that tests only for 0 would read it as free, so
# callers must treat everything but 1 as busy (start-server.sh does).
# Read-only. A port with any trace belongs to the owner until the owner says otherwise.
set -u
shopt -s nullglob
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
slt_require_linux

usage="list-ports.sh [--suggest | --has <port> | --listening <port>]"
case "${1:-}" in
  '' | --suggest) [ $# -le 1 ] || slt_usage "unexpected argument: ${2:-}" "$usage" ;;
  --has | --listening)
    [ $# -eq 2 ] || slt_usage "$1 needs exactly one port" "$usage"
    slt_is_port "$2" || slt_usage "$1 needs a port number (1024-65535), got '$2'" "$usage"
    ;;
  *) slt_usage "unknown argument: $1" "$usage" ;;
esac

root="$(slt_config_root)"
exiled="$(slt_exiled_root)"
declare -A ev=()

add() { ev["$1"]+="$2; "; }
numeric() { case "$1" in '' | *[!0-9]*) return 1 ;; *) return 0 ;; esac; }

# Per-port server config, logs and LabAPI folders.
for sub in config LocalAdminLogs ServerLogs LabAPI/plugins LabAPI/dependencies LabAPI/configs; do
  for d in "$root/$sub"/*/; do
    n="$(basename "$d")"
    numeric "$n" && add "$n" "$sub/$n"
  done
done
# LabAPI loader config.
for f in "$root"/LabAPI/LabApi-*.yml; do
  n="${f##*/LabApi-}"
  n="${n%.yml}"
  numeric "$n" && add "$n" "LabAPI/LabApi-$n.yml"
done
# EXILED per-port files.
for f in "$exiled"/Configs/*-config.yml "$exiled"/Configs/*-translations.yml; do
  n="$(basename "$f")"
  n="${n%%-*}"
  numeric "$n" && add "$n" "EXILED/Configs/$(basename "$f")"
done
declare -A exn=()
for f in "$exiled"/Configs/Plugins/*/*.yml "$exiled"/Configs/Translations/*/*.yml; do
  n="$(basename "$f" .yml)"
  numeric "$n" && exn["$n"]=$((${exn[$n]:-0} + 1))
done
for n in "${!exn[@]}"; do
  add "$n" "EXILED/Configs/{Plugins,Translations}/*/$n.yml (${exn[$n]} files)"
done
# EXILED's Remote Admin command log is Logs/<port>-RAlog.txt (seen on a real machine). The name
# <port>-RemoteAdminLog.txt belongs to an unused path property; kept as a trace in case a build writes it.
for f in "$exiled"/Logs/*-RAlog.txt "$exiled"/*-RemoteAdminLog.txt; do
  n="$(basename "$f")"
  n="${n%%-*}"
  numeric "$n" && add "$n" "EXILED/${f#"$exiled"/}"
done

# Ports something is listening on right now (a live server may have no folders we can see). `ss` first,
# /proc/net otherwise (lib.sh). When neither works the answer is UNKNOWN, never "nothing": a missing tool
# must not turn a busy port into a free one.
listening=" "
listen_known=1
if ports="$(slt_listening_ports)"; then
  while IFS= read -r p; do
    numeric "$p" && listening+="$p "
  done <<<"$ports"
else
  listen_known=0
fi
cannot_tell() {
  echo "cannot tell which ports are listening: neither ss nor /proc/net/udp is usable here. Treating every port as possibly busy." >&2
  exit 3
}

case "${1:-}" in
  --has)
    p="${2:-}"
    [ -n "${ev[$p]:-}" ] && exit 0
    [ "$listen_known" -eq 1 ] || cannot_tell
    case "$listening" in *" $p "*) exit 0 ;; esac
    exit 1
    ;;
  --listening)
    [ "$listen_known" -eq 1 ] || cannot_tell
    case "$listening" in *" ${2:-} "*) exit 0 ;; esac
    exit 1
    ;;
  --suggest)
    [ "$listen_known" -eq 1 ] || cannot_tell
    p=9999
    while [ "$p" -ge 9000 ]; do
      if [ -z "${ev[$p]:-}" ]; then
        case "$listening" in *" $p "*) ;; *)
          echo "$p"
          exit 0
          ;;
        esac
      fi
      p=$((p - 1))
    done
    echo "no free port in 9000-9999: ask the user for one" >&2
    exit 1
    ;;
esac

echo "config root: $root"
if [ "${#ev[@]}" -eq 0 ]; then
  echo "no port traces found (fresh install, or the config root is somewhere else: ask)"
else
  echo "ports with traces (the owner's, do not test on them unless the owner asks):"
  for p in $(printf '%s\n' "${!ev[@]}" | sort -n); do
    echo "  $p  ${ev[$p]}"
  done
fi
if [ "$listen_known" -eq 1 ]; then
  echo "listening now: $(echo "$listening" | xargs)"
else
  echo "listening now: UNKNOWN (neither ss nor /proc/net/udp is usable); treat every port as possibly busy"
fi
if command -v pgrep >/dev/null 2>&1; then
  procs="$(pgrep -af 'LocalAdmin' 2>/dev/null | grep -v pgrep || true)"
  [ -n "$procs" ] && printf 'running LocalAdmin processes:\n%s\n' "$procs"
fi
exit 0
