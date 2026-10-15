#!/usr/bin/env bash
# Deploy checks for LabAPI plugins. Two modes, exit 0 = clean, 1 = findings, 2 = usage.
#
#   verify-deploy.sh layout <LabAPI-dir> <port>
#       BEFORE the restart. Static checks of the folders the loader will scan for that port:
#       duplicate DLLs, game DLLs shipped by mistake, backup-looking names, and a plugin in
#       plugins/global that seems to need a library found only in dependencies/<port>.
#
#   verify-deploy.sh log <boot-log> <PluginName> [<PluginName> ...]
#       AFTER the restart. Every plugin must have its "Successfully enabled" line and the log must
#       hold none of the loader failure strings. PluginName is Plugin.Name, not the DLL name.
#
# <LabAPI-dir> is <config root>/LabAPI (see running-a-test-server/paths.md for the defaults).
# Works on Linux and macOS; on Windows only under Git Bash or WSL (untested there).

set -u

usage() {
  sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
}

errors=0
fail() { echo "FAIL  $*"; errors=$((errors + 1)); }
warn() { echo "WARN  $*"; }
ok()   { echo "ok    $*"; }

# Game or runtime assemblies must never sit in plugins/ or dependencies/.
is_core_dll() {
  case "$(basename "$1" | tr '[:upper:]' '[:lower:]')" in
    labapi.dll|assembly-csharp.dll|assembly-csharp-firstpass.dll|mirror.dll|northwoodlib.dll|\
    pooling.dll|commandsystem.core.dll|yamldotnet.dll|mscorlib.dll|netstandard.dll|unityengine*.dll)
      return 0 ;;
  esac
  return 1
}

looks_like_backup() {
  case "$(echo "$1" | tr '[:upper:]' '[:lower:]')" in
    *.old.dll|*.bak.dll|*.backup.dll|*.orig.dll|*-copy.dll|*" - copy.dll"|*"(1).dll"|*/old/*|*/backup/*|*/bak/*)
      return 0 ;;
  esac
  return 1
}

mode_layout() {
  local root="$1" port="$2"
  [ -d "$root" ] || { echo "no such directory: $root"; exit 2; }

  local dirs=() d
  for d in "plugins/global" "plugins/$port" "dependencies/global" "dependencies/$port"; do
    [ -d "$root/$d" ] && dirs+=("$root/$d")
  done
  [ "${#dirs[@]}" -gt 0 ] || { echo "none of plugins|dependencies / global|$port exist under $root"; exit 2; }

  local all f base seen=""
  all="$(find "${dirs[@]}" -type f -iname '*.dll' 2>/dev/null | sort)"
  [ -n "$all" ] || { echo "no DLLs found"; exit 0; }

  # 1. one copy of each DLL (the loader scans recursively and dedupes nothing)
  local dup
  dup="$(echo "$all" | while IFS= read -r f; do basename "$f" | tr '[:upper:]' '[:lower:]'; done | sort | uniq -d)"
  if [ -n "$dup" ]; then
    echo "$dup" | while IFS= read -r base; do echo "FAIL  duplicate DLL name: $base"; done
    errors=$((errors + $(echo "$dup" | wc -l)))
    echo "$all" | while IFS= read -r f; do
      echo "$dup" | grep -qxF "$(basename "$f" | tr '[:upper:]' '[:lower:]')" && echo "        $f"
    done
  else
    ok "no duplicate DLL names"
  fi

  # 2. game assemblies and backup-looking files
  local core=0 bak=0
  while IFS= read -r f; do
    if is_core_dll "$f"; then fail "game/runtime assembly must not be deployed: $f"; core=1; fi
    if looks_like_backup "$f"; then warn "backup-looking file is still loaded (every *.dll below is): $f"; bak=1; fi
  done <<EOF
$all
EOF
  [ "$core" -eq 0 ] && ok "no game assemblies deployed"
  [ "$bak" -eq 0 ] && ok "no backup-looking DLLs"

  # 3. a global plugin cannot see a library that exists only for one port.
  #    Heuristic: assembly references are plain UTF-8 names in the DLL's metadata, so a grep for the
  #    library's file stem finds them. A hit is a candidate to check, not proof.
  local gp="$root/plugins/global" pd="$root/dependencies/$port" gd="$root/dependencies/global"
  local hits=0 plugin dep stem
  if [ -d "$gp" ] && [ -d "$pd" ]; then
    while IFS= read -r plugin; do
      while IFS= read -r dep; do
        stem="$(basename "$dep")"; stem="${stem%.[dD][lL][lL]}"
        [ -f "$gd/$(basename "$dep")" ] && continue
        [ "$(basename "$plugin")" = "$(basename "$dep")" ] && continue
        if grep -qaF "$stem" "$plugin"; then
          fail "global plugin $(basename "$plugin") seems to reference $stem, which is only in dependencies/$port: MOVE it to dependencies/global, not copy: one copy only (other ports fail with ReflectionTypeLoadException)"
          hits=1
        fi
      done < <(find "$pd" -type f -iname '*.dll' 2>/dev/null | sort)
    done < <(find "$gp" -type f -iname '*.dll' 2>/dev/null | sort)
  fi
  [ "$hits" -eq 0 ] && ok "no global plugin seems to need a port-only dependency (heuristic)"
}

# Strings the loader logs on failure (see deploying-scpsl-plugins / verify-and-brand.md).
FAIL_RE="Couldn't load the plugin|Couldn't enable the plugin|Couldn't load the dependency|Failed to load the configuration|Failed to resolve embedded resources|Unable to register command|Couldn't read the configuration|Couldn't create a default instance|Potential issue with plugin|Forcefully loading unsupported|ReflectionTypeLoadException"

mode_log() {
  local log="$1"; shift
  [ -r "$log" ] || { echo "cannot read log: $log"; exit 2; }
  [ "$#" -gt 0 ] || usage

  local name
  for name in "$@"; do
    if grep -qF "Successfully enabled '$name'" "$log"; then
      ok "Successfully enabled '$name'"
    else
      fail "no 'Successfully enabled' line for '$name' (loader error, wrong folder, or is_enabled: false in properties.yml, which logs nothing)"
    fi
  done

  local bad
  bad="$(grep -nE "$FAIL_RE" "$log")"
  if [ -n "$bad" ]; then
    fail "loader failure strings in the log:"
    echo "$bad" | sed 's/^/        /'
  else
    ok "no loader failure strings"
  fi
  echo "(a log file is one boot; make sure this is the log of the restart you just did)"
}

[ "$#" -ge 1 ] || usage
mode="$1"; shift
case "$mode" in
  layout) [ "$#" -eq 2 ] || usage; mode_layout "$1" "$2" ;;
  log)    [ "$#" -ge 2 ] || usage; mode_log "$@" ;;
  *)      usage ;;
esac

if [ "$errors" -gt 0 ]; then
  echo "$errors problem(s)"
  exit 1
fi
echo "clean"
