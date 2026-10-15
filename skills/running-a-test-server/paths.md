# Default paths: install, config, logs

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

Look here and run `bash scripts/find-install.sh`. If the user's layout differs, ASK for the install folder and the config root, then use them.

## Looking for the install: the one rule

Allowed, and nothing else:

1. The default install paths in the tables below.
2. The library folders Steam lists in `steamapps/libraryfolders.vdf` (the `"path"` entries), read from the Steam client folders `${XDG_DATA_HOME:-~/.local/share}/Steam`, `~/.steam/steam` and the Flatpak path below.
3. A path the user gave you.

Not allowed: `find`, `locate`, `grep -r`, `ls -R`, `du`, or any other walk over the home folder, `/`, mounted drives or `/opt`. That holds for you and for scripts. `find-install.sh` and `start-server.sh` (without `--install`) do exactly 1 and 2 and nothing more; the tests pin this. Nothing found: ask. SteamCMD installs and custom folders are the usual reason, and only the owner knows them.

(`list-created.sh` walks the config root, the EXILED root and the install folder AFTER the install is known: those are known folders, not a search.)

Status column: **code** = derived from the public LabAPI/EXILED source or the game's path logic; **seen** = observed on a real Linux server machine; **unverified** = Windows or a convention not run here. Everything Windows is unverified.

## Linux

| What | Default | Status |
|---|---|---|
| Install, Steam client library | `~/.local/share/Steam/steamapps/common/SCP Secret Laboratory Dedicated Server` (`~/.steam/steam` is usually a symlink to the same place) | seen |
| Install, other Steam library | `<library>/steamapps/common/SCP Secret Laboratory Dedicated Server`; libraries are the `"path"` entries of `~/.local/share/Steam/steamapps/libraryfolders.vdf` | seen (format) |
| Install, Flatpak Steam | `~/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/...` | unverified |
| Install, SteamCMD | wherever `force_install_dir` pointed (app id 996560). Unknowable: ask | n/a |
| Install contents | `LocalAdmin` (launcher), `SCPSL.x86_64` (game), `SCPSL_Data/Managed/` (assemblies to compile against), `ConfigTemplates/`, `hoster_policy.txt` only if the operator made one | seen |
| Config root (`<root>`) | `${XDG_CONFIG_HOME:-~/.config}/SCP Secret Laboratory` | code (`ApplicationData`), seen for `~/.config`; the XDG override is unverified |
| Per-port server config | `<root>/config/<port>/` with `config_gameplay.txt`, `config_remoteadmin.txt`, `config_sharing.txt`, `UserIdBans.txt`, `IpBans.txt`, whitelist and reserved-slot files | code, seen |
| Not ports | `<root>/config/nondedicated`, `config_localadmin_global.txt`, `localadmin_internal_data.json` | seen |
| EULA record | `<root>/config/localadmin_internal_data.json`, key `"EulaAccepted"` holds an ISO timestamp after a server was run there. `start-server.sh` only READS it; never write it. Not verified: whether the prompt, the flag or both set it, and its value on a never-accepted install (assumed `null`) | seen (one machine) |
| LabAPI | `<root>/LabAPI/{plugins,dependencies,configs}/{global,<port>}` and `<root>/LabAPI/LabApi-<port>.yml` | code, seen |
| LocalAdmin log | `<root>/LocalAdminLogs/<port>/LocalAdmin Log <yyyy-MM-dd HH.mm.ss>.txt`, one per start | seen |
| Game round logs | `<root>/ServerLogs/<port>/`; logs older than 7 days are zipped to `Round Logs Archive <date>.zip` at start | live run |
| EXILED root | `<parent of root>/EXILED` = `~/.config/EXILED`: `Plugins/`, `Plugins/dependencies/`, `Configs/`, `Logs/` | code, seen |
| EXILED per-port files | `Plugins/<port>/` (EXILED plugins for one port), `Configs/Plugins/<Plugin>/<port>.yml` and `Logs/<port>-RAlog.txt` (its Remote Admin command log) | code, seen |
| EXILED per-port, only in some modes | `Configs/Translations/<Plugin>/<port>.yml`; `Configs/<port>-config.yml` and `<port>-translations.yml` exist only in merged-config mode. A path property `<port>-RemoteAdminLog.txt` exists in code but nothing writes it | code |

## Windows (unverified, scripts do not run there)

Nothing in this section was observed on a Windows machine, and the scripts do not support Windows at all (Linux only; Git Bash included). Use the table to know where to look by hand; the manual procedure is in launch-and-console.md.

| What | Default |
|---|---|
| Install, Steam client library | `C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server`; other drives via `steamapps\libraryfolders.vdf` |
| Install, SteamCMD | wherever `force_install_dir` pointed: ask |
| Launcher and game | `LocalAdmin.exe`, `SCPSL.exe` (names assumed from the Linux layout) |
| Config root | `%APPDATA%\SCP Secret Laboratory` (`C:\Users\<user>\AppData\Roaming\...`): LabAPI and the game both use .NET's `ApplicationData` folder (code) |
| LabAPI, logs, per-port config | same relative layout as Linux under that root |
| EXILED | `%APPDATA%\EXILED` |

A server run as a Windows service or under another account has THAT account's `%APPDATA%`. If `%APPDATA%\SCP Secret Laboratory` is missing or empty, ask who runs the server.

## What moves the config root

- `hoster_policy.txt` with a line `gamedir_for_configs: true`, read from the working directory or else from the config root (code). The game then keeps `config/<port>` under a relative `AppData` folder (`<launch dir>/AppData/config/<port>`), while LabAPI puts its tree one level deeper, `<launch dir>/AppData/SCP Secret Laboratory/LabAPI` (code). EXILED computes its own root from `ApplicationData` and ignores the policy (code). So under a policy the three live in different places, and the scripts' single `SLT_CONFIG_ROOT` cannot cover them: ask the user, inspect by hand. `find-install.sh` reports the file.
- `SLT_CONFIG_ROOT` and `SLT_INSTALL` override the detection in the scripts. Use them after the user gives you a non-default path.

## Remember the answer

Once the user names a non-default path, reuse it for the whole session and say so ("using `<path>` as you told me"). Offer to put it in the project's notes file; write it there only if they agree.

## Re-verifying on a new build

- Re-read `PathManager` in LabAPI and the game's `FileManager.GetAppFolder` for the folder names (`SCP Secret Laboratory`, `config/<port>`, `LabAPI`), and EXILED's `Paths.Reload`.
- On a Windows machine: start a server once, run `dir %APPDATA%\"SCP Secret Laboratory"`, and replace every "unverified" above with what you saw.
- Check the LocalAdmin log file name pattern and whether the old-log archive threshold (7 days) changed.
