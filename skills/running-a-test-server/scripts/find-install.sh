#!/usr/bin/env bash
# Usage: bash scripts/find-install.sh
# Prints the SCP:SL dedicated-server install folders found in the default places, plus the
# config roots. Exit 0 when at least one install is found, 1 when none is (then ASK the user).
# Read-only: it never creates or changes anything.
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$here/lib.sh"
slt_require_linux
[ $# -eq 0 ] || slt_usage "takes no arguments, got '$1'" "find-install.sh"

echo "os: $(slt_os)"
root="$(slt_config_root)"
echo "config root: ${root:-<unknown>} $([ -d "$root" ] && echo '(exists)' || echo '(missing)')"
exiled="$(slt_exiled_root)"
echo "exiled root: $exiled $([ -d "$exiled" ] && echo '(exists)' || echo '(missing)')"

found=0
while IFS= read -r dir; do
  [ -z "$dir" ] && continue
  found=$((found + 1))
  echo "install: $dir"
  # The game reads hoster_policy.txt from the working directory, else from the config root.
  for pol in "$dir/hoster_policy.txt" "$root/hoster_policy.txt"; do
    if [ -f "$pol" ] && grep -qi 'gamedir_for_configs: true' "$pol"; then
      echo "  note: $pol sets gamedir_for_configs: the game keeps config/<port> in '<launch dir>/AppData' and LabAPI in '<launch dir>/AppData/SCP Secret Laboratory/LabAPI'. Ask where the server is launched from, then pass SLT_CONFIG_ROOT"
    fi
  done
done < <(slt_find_installs)

if [ "$found" -eq 0 ]; then
  echo "NOT FOUND in the default places. Ask the user for the install folder (the one that contains LocalAdmin)." >&2
  echo "If they installed with SteamCMD it is wherever force_install_dir pointed. Pass it on with SLT_INSTALL=<dir> or --install." >&2
  exit 1
fi
if [ "$found" -gt 1 ]; then
  echo "More than one install found: ask which one the user wants." >&2
fi
exit 0
