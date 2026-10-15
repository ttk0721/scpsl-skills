# Command examples

Verified against LabAPI 1.1.7 / SCP:SL 14.2.7 (2026-10). Original code, not compiled in CI: confirm namespaces against your references. Part of scpsl-commands-and-permissions.

Notation: `CheckPermission` is the game's extension on `ICommandSender`; `HasPermission(string)` comes from `LabApi.Features.Permissions`.

## 1. RA + console command, game flag, optional player target

One class, two handlers: two instances, so shared state is `static`.

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using CommandSystem;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using RemoteAdmin;

[CommandHandler(typeof(RemoteAdminCommandHandler))]
[CommandHandler(typeof(GameConsoleCommandHandler))]
public sealed class MpNotify : ICommand, IUsageProvider
{
    public string Command => "mp_notify";
    public string[] Aliases => Array.Empty<string>();           // never null
    public string Description => "Sends a hint to one player.";
    public string[] Usage => new[] { "%player%", "Seconds", "Text" };

    // Per class instance state would be duplicated per handler: keep it static.
    private static readonly Dictionary<int, long> LastUse = new Dictionary<int, long>();
    private static readonly long Cooldown = Stopwatch.Frequency * 2;

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        try
        {
            // 1. Permission first. Game flag; the server console passes automatically.
            if (!sender.CheckPermission(PlayerPermissions.PlayersManagement, out response))
                return false;

            // 2. Arguments: Count first, then At(i). Index 0 is the first ARGUMENT, not the command word.
            if (arguments.Count < 3)
            {
                response = "Usage: mp_notify <id> <seconds> <text...>";
                return false;
            }
            if (!int.TryParse(arguments.At(0), out int id) || Player.Get(id) is not { } target || target.IsHost)
            {
                response = "Player not found.";
                return false;
            }
            if (!float.TryParse(arguments.At(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)
                || seconds <= 0f || seconds > 30f)
            {
                response = "Seconds must be 0-30.";
                return false;
            }
            // Tail joined by hand: no quote parsing in the game.
            string text = string.Join(" ", arguments.Array, arguments.Offset + 2, arguments.Count - 2);

            // 3. Sender may be the server console: only players get a cooldown.
            if (sender is PlayerCommandSender pcs)
            {
                long now = Stopwatch.GetTimestamp();
                if (LastUse.TryGetValue(pcs.PlayerId, out long last) && now - last < Cooldown)
                {
                    response = "Slow down.";
                    return false;
                }
                LastUse[pcs.PlayerId] = now;
            }

            target.SendHint(text, seconds);
            response = "Sent to " + target.Nickname;      // non-empty or RA/console print nothing
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("mp_notify failed: " + ex);       // full detail in our log only
            response = "Internal error.";                  // short text to the sender
            return false;
        }
    }
}
```

Clear `LastUse` on round boundaries and when players leave (player ids are recycled). See scpsl-game-lifecycle.

## 2. Dot command with a LabAPI string permission

Dot commands have no gate. A separate class, a separate permission string, and a refusal.

```csharp
[CommandHandler(typeof(ClientCommandHandler))]
public sealed class MpMe : ICommand
{
    public string Command => "mp_me";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "Shows your plugin rank.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        // String permission: console allowed, unresolved player denied (safe default).
        if (!sender.HasPermission("myplugin.me"))
        {
            response = "You may not use this.";
            return false;
        }
        if (!Player.TryGet(sender, out Player player))
        {
            response = "Players only.";
            return false;
        }
        player.SendConsoleMessage("Hello " + player.Nickname, "green"); // extra lines go here, not sender.Respond
        response = "done";                                              // dot path prints only this
        return true;
    }
}
```

Matching `permissions.yml` (every RA group needs its own entry, nested wildcards are not recursive):

```yaml
default:
  permissions: []
moderator:
  inherited_groups: [default]
  permissions:
    - myplugin.me
owner:
  permissions:
    - .*
```

## 3. Parent command with sub-commands

```csharp
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class Mp : ParentCommand
{
    public override string Command => "mp";
    public override string[] Aliases => Array.Empty<string>();
    public override string Description => "MyPlugin commands.";
    public override void LoadGeneratedCommands() { }        // children register via their own attribute

    protected override bool ExecuteParent(ArraySegment<string> a, ICommandSender s, out string r)
        => LabApi.Loader.Features.Commands.Extensions.CommandExtensions.ListSubCommands(this, a, out r);
}

[CommandHandler(typeof(Mp))]                                // child of the parent type
public sealed class MpStatus : ICommand
{
    public string Command => "status";
    public string[] Aliases => Array.Empty<string>();       // null here would make the parent's list throw
    public string Description => "Shows plugin status.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!sender.HasPermission("myplugin.status")) { response = "Denied."; return false; }
        response = "ok";
        return true;
    }
}
```

The parent needs its own `[CommandHandler(...)]` attribute or the child is not registered. Invoked as `mp status`.

## 4. Slow work, reply later (SlKit)

```csharp
public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
{
    if (!sender.CheckPermission(PlayerPermissions.ServerConsoleCommands, out response)) return false;
    if (!(sender is PlayerCommandSender p)) { response = "Players only."; return false; }

    int callerId = p.PlayerId;
    string query = arguments.Count > 0 ? arguments.At(0) : "";           // plain snapshot

    // MyPlugin.Worker is this plugin's own SlKit worker, made once in Enable:
    //   Worker = SlKit.Background.ForOwner(Name, capacity: 32);
    // The static Background.TryRun would queue behind every other plugin's jobs on one shared worker.
    bool queued = MyPlugin.Worker.TryRun(
        () => ExpensiveLookup(query),                                     // worker thread: plain data only
        result =>                                                         // main thread
        {
            Player caller = Player.Get(callerId);
            if (caller == null || caller.IsDestroyed) return;             // left meanwhile
            caller.SendConsoleMessage(result, "cyan");
        },
        error => Logger.Warn("lookup failed: " + error.Message));

    response = queued ? "Working..." : "Busy, try again.";
    return queued;
}
```

(`Background`, `MainThread` and `SlHttp` are SlKit members: see tools/SlKit/README.md for exact signatures. For HTTP use the plugin's own `SlHttp.CreateClient` client in the same way.)

## 5. Startup self-check for collisions

```csharp
public override void Enable()
{
    foreach (string name in new[] { "mp_notify", "mp" })
    {
        if (Server.RemoteAdminCommandHandler.TryGetCommand(name, out ICommand c)
            && c.GetType().Assembly != typeof(MyPlugin).Assembly)
            Logger.Warn("Command '" + name + "' is owned by another assembly; ours was skipped.");
    }
}
```

Note that `TryGetCommand` resolves aliases first, so this also catches a built-in alias equal to your name.

## Re-verifying on a new build

- Compile the snippets against the target `LabApi.dll` and `Assembly-CSharp.dll`; fix namespaces if `CheckPermission`, `At` or `Player.TryGet` moved.
- Run example 1 and 2 on a test server as RA, console and dot sender; confirm the silent-failure claims (empty response, colliding name) still hold.
