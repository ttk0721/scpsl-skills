using System;
using System.Collections.Generic;
using CommandSystem;
using LabApi.Features.Console;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;
using RemoteAdmin;

namespace Examples.PortingExiledPairs
{
    /// <summary>
    /// Pair 7 (permissions). The EXILED original:
    /// <code>
    /// if (!sender.CheckPermission("pairs.heal")) { response = "no permission"; return false; }   // Exiled.Permissions.Extensions
    /// if (!ev.Player.CheckPermission("pairs.heal")) return;                                       // on a Player
    /// </code>
    /// LabAPI: <c>HasPermission(string)</c> (extension from <c>LabApi.Features.Permissions</c>) on both
    /// <c>ICommandSender</c> and <c>Player</c>; the game's <c>PlayerPermissions</c> flags are a different overload
    /// (<c>sender.CheckPermission(PlayerPermissions.X, out response)</c>) and the two do not mix.
    /// The string check reads <c>LabAPI/configs/permissions.yml</c>, whose semantics differ from EXILED's (see
    /// replacements.md): the wildcard covers only the immediate parent, matching is case-sensitive, groups assigned
    /// at runtime are ignored. For a command sender that is not a player (the server console) the check passes.
    /// It also returns false inside <c>Enable()</c>, because the provider registers after plugins enable: call it
    /// only at runtime, never at startup.
    /// </summary>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class HealCommand : ICommand
    {
        private const string Permission = "pairs.heal";

        public string Command => "pairsheal";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Heals one player, or every alive player, by an amount (permission pairs.heal).";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            try
            {
                PortingPairsPlugin plugin = PortingPairsPlugin.Instance;
                if (plugin == null)
                {
                    response = "PortingPairs is disabled.";
                    return false;
                }

                PairsTranslation text = plugin.Text;
                if (!sender.HasPermission(Permission))
                {
                    response = text.NoPermission;
                    return false;
                }

                if (arguments.Count < 1)
                {
                    response = text.HealUsage;
                    return false;
                }

                float amount = 25f;
                if (arguments.Count >= 2 && (!float.TryParse(arguments.At(1), out amount) || !(amount > 0f) || amount > 1000f))
                {
                    response = text.HealUsage;
                    return false;
                }

                int healed = 0;
                string target = arguments.At(0);
                if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    // Snapshot first, then act: nothing is copied for you in LabAPI (EXILED's Player.List was a copy).
                    List<Player> players = new List<Player>(64);
                    PlayerScan.Snapshot(players);
                    for (int i = 0; i < players.Count; i++)
                    {
                        if (players[i].IsDestroyed || !players[i].IsAlive) continue;
                        players[i].Heal(amount);
                        healed++;
                    }
                }
                else
                {
                    int id;
                    Player player;
                    if (!int.TryParse(target, out id) || !Player.TryGet(id, out player) || player.IsHost || !player.IsAlive)
                    {
                        response = "No such alive player.";
                        return false;
                    }

                    player.Heal(amount);
                    healed = 1;
                }

                response = text.Healed.Replace("{count}", healed.ToString()).Replace("{amount}", amount.ToString("0.#"));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("pairsheal failed: " + ex);   // full detail in our log only
                response = "Internal error.";
                return false;
            }
        }
    }
}
