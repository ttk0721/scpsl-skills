using System;
using System.Globalization;
using CommandSystem;
using LabApi.Features.Wrappers;
using RemoteAdmin;
using UnityEngine;

namespace Examples.NetworkingSnippets
{
    /// <summary>
    /// netdemo discs [n]                    queue n static discs on a ring around you
    /// netdemo group                        queue one anchor with four static children
    /// netdemo move                         move the group (or the first disc) to your position
    /// netdemo scale [viewerId targetId factor|off]
    /// netdemo ghost [playerId] [off]       hide a player from everyone but you
    /// netdemo status                       counts: queue, movers, fake scales, ghosts
    /// Remote Admin only. Run it on a TEST server: spawns and hides affect every connected player.
    /// </summary>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class NetDemoCommand : ICommand
    {
        private const string Usage = "Usage: netdemo discs [n] | group | move | scale <viewerId> <targetId> <factor|off> | ghost <playerId> [off] | status";

        public string Command => "netdemo";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Exercises the networking-snippets patterns (test servers only).";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            try
            {
                if (!sender.CheckPermission(PlayerPermissions.FacilityManagement, out response))
                    return false;

                NetworkingPlugin plugin = NetworkingPlugin.Instance;
                if (plugin == null || plugin.Config == null || !plugin.Config.IsEnabled)
                {
                    response = "NetworkingSnippets is disabled.";
                    return false;
                }

                if (arguments.Count < 1)
                {
                    response = Usage;
                    return false;
                }

                Player.TryGet(sender, out Player self); // null for the server console

                switch (arguments.At(0).ToLowerInvariant())
                {
                    case "discs":
                        return Discs(arguments, plugin, self, out response);
                    case "group":
                        return Group(plugin, self, out response);
                    case "move":
                        return Move(plugin, self, out response);
                    case "scale":
                        return Scale(arguments, plugin, out response);
                    case "ghost":
                        return Ghost(arguments, plugin, self, out response);
                    case "status":
                        response = "queue " + plugin.Queue.Pending + " pending / " + plugin.Queue.Reserved + " reserved, movers "
                            + plugin.Mover.MovingCount + ", fake scales " + plugin.FakeScale.Count + ", ghosts " + plugin.Ghosts.Count;
                        return true;
                    default:
                        response = Usage;
                        return false;
                }
            }
            catch (Exception ex)
            {
                LabApi.Features.Console.Logger.Error("netdemo failed: " + ex); // full detail in our log only
                response = "Internal error.";
                return false;
            }
        }

        private static bool Discs(ArraySegment<string> args, NetworkingPlugin plugin, Player self, out string response)
        {
            if (self == null)
            {
                response = "Players only (the ring is built around you).";
                return false;
            }

            int count = 8;
            if (args.Count >= 2 && (!int.TryParse(args.At(1), out count) || count < 1 || count > 64))
            {
                response = "n must be 1-64.";
                return false;
            }

            int queued = plugin.QueueDiscs(self.Position, count);
            response = "Queued " + queued + " of " + count + " discs (cap " + plugin.Config.MaxToys + " identities; spawned in slices).";
            return queued > 0;
        }

        private static bool Group(NetworkingPlugin plugin, Player self, out string response)
        {
            if (self == null)
            {
                response = "Players only.";
                return false;
            }

            bool queued = plugin.QueueGroup(self.Position + Vector3.up * 1.5f);
            response = queued ? "Group queued." : "A group already exists, or the identity cap would be exceeded.";
            return queued;
        }

        private static bool Move(NetworkingPlugin plugin, Player self, out string response)
        {
            if (self == null)
            {
                response = "Players only.";
                return false;
            }

            PrimitiveObjectToy toy = plugin.Group != null ? plugin.Group.Anchor : (plugin.Discs.Count > 0 ? plugin.Discs[0] : null);
            if (toy == null)
            {
                response = "Nothing spawned yet (try netdemo group, then wait a moment).";
                return false;
            }

            bool changed = plugin.Mover.MoveTo(toy, self.Position + Vector3.up * 1.5f);
            response = changed
                ? "Moving. It freezes again after " + plugin.Mover.SettleSeconds().ToString("0.00", CultureInfo.InvariantCulture) + " s without a new target."
                : "No change (same target, or the toy is gone).";
            return true;
        }

        private static bool Scale(ArraySegment<string> args, NetworkingPlugin plugin, out string response)
        {
            if (args.Count < 4
                || !int.TryParse(args.At(1), out int viewerId) || !int.TryParse(args.At(2), out int targetId)
                || !Player.TryGet(viewerId, out Player viewer) || !Player.TryGet(targetId, out Player target))
            {
                response = "Usage: netdemo scale <viewerId> <targetId> <factor|off>";
                return false;
            }

            if (args.At(3).Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                plugin.FakeScale.Clear(viewer, target);
                response = "Real scale restored for that viewer.";
                return true;
            }

            if (!float.TryParse(args.At(3), NumberStyles.Float, CultureInfo.InvariantCulture, out float factor))
            {
                response = "factor must be a number.";
                return false;
            }

            string refusal = plugin.FakeScale.Set(viewer, target, new Vector3(factor, factor, factor));
            response = refusal ?? (viewer.Nickname + " now sees " + target.Nickname + " at x" + factor.ToString("0.##", CultureInfo.InvariantCulture) + ".");
            return refusal == null;
        }

        private static bool Ghost(ArraySegment<string> args, NetworkingPlugin plugin, Player self, out string response)
        {
            Player target = self;
            if (args.Count >= 2 && (!int.TryParse(args.At(1), out int id) || !Player.TryGet(id, out target)))
            {
                response = "Player not found.";
                return false;
            }
            if (target == null)
            {
                response = "The server console must name a player id.";
                return false;
            }

            if (args.Count >= 3 && args.At(2).Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                response = plugin.Ghosts.Reveal(target) ? target.Nickname + " is being revealed." : target.Nickname + " was not a ghost.";
                return true;
            }

            plugin.Ghosts.ExemptViewer(self); // the admin keeps seeing the ghost; null (console) is ignored
            if (!plugin.Ghosts.TryGhost(target, out string refusal))
            {
                response = "Refused: " + refusal + ".";
                return false;
            }

            response = target.Nickname + " is hidden from other players after " + plugin.Config.GhostHideDelaySeconds.ToString("0.#", CultureInfo.InvariantCulture)
                + " s. Footsteps, voice and shots are silent by design.";
            return true;
        }
    }
}
