---
name: scpsl-commands-and-permissions
description: "Use when adding or debugging Remote Admin (RA), server-console or client-console (dot) commands in a LabAPI plugin, or permission checks for them. Symptoms - command silently missing or \"Unknown command\", CommandHandler attribute, ICommand/ParentCommand, ArraySegment indexer error, HasPermission/HasPermissions/CheckPermission/PlayerPermissions compile error, RA shows no output, console sender NRE, players kicked for rate limit."
---

# Commands and Permissions (LabAPI)

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10).

## Overview

A command is a class with `[CommandHandler(typeof(<handler>))]`; LabAPI registers it before `Enable()`. Three namespaces (RA, console, client dot), two unrelated permission systems (game flags, LabAPI strings). Mixing them up fails to compile or fails silently.

## When to Use

- Writing any `ICommand` / `ParentCommand`, or gating one by permission.
- A command "does nothing", prints nothing, or the wrong built-in answers.
- NOT for keybinds/menus (scpsl-server-specific-settings) or hints (scpsl-hints-and-hud).

## Quick Reference

| Need | Use |
|---|---|
| RA / server console / dot command | `[CommandHandler(typeof(RemoteAdminCommandHandler / GameConsoleCommandHandler / ClientCommandHandler))]`, public parameterless ctor |
| Sub-command | `[CommandHandler(typeof(MyParent))]`; the parent needs its own attribute |
| Game flag check | `sender.CheckPermission(PlayerPermissions.X, out response)`; array = ANY-of, server console always passes |
| Plugin-defined right | `sender.HasPermission("myplugin.action")` (string, `permissions.yml`); console allowed |
| Read argument | `arguments.At(i)` after `arguments.Count`; no `[i]` on net48 |
| Resolve player | `Player.TryGet(sender, out var p)` or `sender is PlayerCommandSender` |
| Reply | non-empty `out response`; dot commands: also `Player.SendConsoleMessage` |

## Rules

1. Name commands `myplugin_x` or hang them under one `ParentCommand`. A duplicate name is skipped with NO log; a built-in alias (`pl`, `list`, `sg`) shadows you because aliases resolve first. (The LabAPI example `hello` never registers.)
2. `Aliases` returns `Array.Empty<string>()`, never null. An alias equal to another command's name hijacks it.
3. Always set a non-empty `response`; RA and console print nothing otherwise. Do not call `sender.Respond` from a dot command (goes to the RA channel).
4. Check permission first thing in `Execute`, in every handler class. Dot commands are open to every connected client.
5. Treat the sender as possibly not a player: the server console has no hub. Use one class per handler; RA and dot senders look identical.
6. Arguments split on single spaces only: no quotes, no empty tokens, case-sensitive. Join the tail yourself.
7. `Execute` runs on the main thread inside the tick. No `.Result`, no sync HTTP, no file IO loops. Offload and reply later (**REQUIRED SUB-SKILL:** scpsl-main-thread-and-timing).
8. One shared 150 uses / 3 s bucket covers RA, dot and admin chat and by default KICKS on overflow. Add a per-player cooldown for anything expensive; use SSS events for frequent actions.
9. Register at plugin load, never lazily (late commands probably miss RA autocomplete; inferred). `refreshcommands` wipes all RA/console plugin commands; dot survive.
10. Never pass player text to `Server.RunCommand`: default sender is the console with full rights.
11. Wrap bodies in try/catch; a thrown exception sends the stack trace to the sender (players, for dot).

```csharp
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class MpCard : ICommand
{
    public string Command => "mp_card";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Example.";
    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.CheckPermission(PlayerPermissions.GivingItems, out response)) return false;
        if (arguments.Count < 1) { response = "Usage: mp_card <id>"; return false; }
        response = "ok";
        return true;
    }
}
```

More: [command-system-reference.md](command-system-reference.md), [permissions-reference.md](permissions-reference.md), [command-cost-and-threading.md](command-cost-and-threading.md), [command-examples.md](command-examples.md).

## Common Mistakes

- **Wrong:** `sender.HasPermissions(PlayerPermissions.GivingItems)`. `HasPermission(s)` from `LabApi.Features.Permissions` take STRINGS (`permissions.yml`); this does not compile, so the command does not exist. **Right:** `sender.CheckPermission(PlayerPermissions.GivingItems, out response)` for game flags.
- **Wrong:** `arguments[0]` (compile error) or `arguments.Array[0]` (that is the command word). **Right:** `arguments.At(0)` after a `Count` check; `At` does not bounds-check against `Count`.
- **Wrong:** `Player.HasPermission(PlayerPermissions.A | PlayerPermissions.B)` to mean "either". It is ALL-of; `CheckPermission` arrays are ANY-of. Use single flags.
- **Wrong:** assuming `a.*` covers `a.b.c`. It covers one level only; matching is case-sensitive. A group missing from `permissions.yml` gets NO rights, not the `default` entry.
- **Wrong:** checking `HasPermission` in `Enable()`. The provider registers after plugins enable, so it returns false there.
- **Wrong:** `Player.AddPermissions` as a per-player grant. It edits the whole group and rewrites the file on the main thread.
