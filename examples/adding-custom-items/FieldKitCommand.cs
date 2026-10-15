using System;
using CommandSystem;
using LabApi.Features.Wrappers;
using RemoteAdmin;
using UnityEngine;

namespace Examples.AddingCustomItems
{
    /// <summary>
    /// fieldkit give [playerId]   gives a kit (drops it at the feet when the inventory is full)
    /// fieldkit spawn             creates one on the floor in front of the sender
    /// Remote Admin only; the game flag GivingItems gates it (the server console always passes).
    /// </summary>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class FieldKitCommand : ICommand
    {
        public string Command => "fieldkit";
        public string[] Aliases => Array.Empty<string>();
        public string Description => "Gives or spawns a field kit (a tagged medkit).";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            try
            {
                if (!sender.CheckPermission(PlayerPermissions.GivingItems, out response))
                    return false;

                FieldKitPlugin plugin = FieldKitPlugin.Instance;
                if (plugin == null)
                {
                    response = "FieldKit is disabled.";
                    return false;
                }

                if (arguments.Count < 1)
                {
                    response = "Usage: fieldkit give [playerId] | fieldkit spawn";
                    return false;
                }

                bool senderIsPlayer = Player.TryGet(sender, out Player self);
                switch (arguments.At(0).ToLowerInvariant())
                {
                    case "give":
                        Player target = self;
                        if (arguments.Count >= 2)
                        {
                            if (!int.TryParse(arguments.At(1), out int id) || !Player.TryGet(id, out target) || target.IsHost)
                            {
                                response = "Player not found.";
                                return false;
                            }
                        }
                        else if (!senderIsPlayer)
                        {
                            response = "The server console must name a player id.";
                            return false;
                        }

                        response = Describe(plugin.Give(target, dropIfFull: true), target.Nickname, plugin);
                        return true;

                    case "spawn":
                        if (!senderIsPlayer)
                        {
                            response = "Players only.";
                            return false;
                        }

                        response = Describe(plugin.SpawnAt(self.Position + Vector3.up * 0.5f, null), self.Nickname, plugin);
                        return true;

                    default:
                        response = "Usage: fieldkit give [playerId] | fieldkit spawn";
                        return false;
                }
            }
            catch (Exception ex)
            {
                LabApi.Features.Console.Logger.Error("fieldkit failed: " + ex);   // full detail in our log only
                response = "Internal error.";
                return false;
            }
        }

        private static string Describe(GiveResult result, string nickname, FieldKitPlugin plugin)
        {
            switch (result)
            {
                case GiveResult.Given: return "Field kit given to " + nickname + ".";
                case GiveResult.DroppedAtFeet: return nickname + " has a full inventory; the kit lies at their feet.";
                case GiveResult.CapReached: return "Cap reached (" + plugin.ActiveCount + " kits exist).";
                default: return "Could not create the kit.";
            }
        }
    }
}
