using System;
using System.Text;
using CommandSystem;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using RemoteAdmin;

namespace Examples.AddingCustomRoles
{
    /// <summary>
    /// Remote Admin command to give a custom role: "customrole list" or "customrole &lt;player id&gt; &lt;role id&gt;".
    /// This is also how a role is tested without waiting for a lucky round-start roll.
    /// </summary>
    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class CustomRoleCommand : ICommand, IUsageProvider
    {
        public string Command => "customrole";
        public string[] Aliases => Array.Empty<string>();           // never null
        public string Description => "Gives a player a custom role: customrole <player id> <role id> | customrole list";
        public string[] Usage => new[] { "%player%", "Role id" };

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            try
            {
                // Permission first. Same right as forcing a class without restrictions.
                if (!sender.CheckPermission(PlayerPermissions.ForceclassWithoutRestrictions, out response))
                    return false;

                CustomRolesPlugin plugin = CustomRolesPlugin.Instance;
                if (plugin == null)
                {
                    response = "CustomRoles is not running.";
                    return false;
                }

                if (arguments.Count == 1 && arguments.At(0) == "list")
                {
                    var sb = new StringBuilder("Custom roles:");
                    foreach (string id in plugin.DefinitionIds()) sb.Append(' ').Append(id);
                    response = sb.ToString();
                    return true;
                }

                if (arguments.Count < 2)
                {
                    response = "Usage: customrole <player id> <role id> | customrole list";
                    return false;
                }
                if (!int.TryParse(arguments.At(0), out int playerId) || Player.Get(playerId) is not { } target)
                {
                    response = "Player not found.";
                    return false;
                }

                if (!plugin.Grant(target, arguments.At(1), out string error))
                {
                    response = error;
                    return false;
                }
                response = target.Nickname + " is now " + arguments.At(1) + ".";    // non-empty or RA prints nothing
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("customrole failed: " + ex);   // full detail in our log only
                response = "Internal error.";
                return false;
            }
        }
    }
}
