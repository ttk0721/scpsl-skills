using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace Examples.LabapiWrappersSnippets
{
    /// <summary>
    /// Operation 8: move and aim a player. Each setter below writes the transform and sends one
    /// uncoalesced position-override message to that client per call, so none of this belongs in a
    /// per-frame loop over players.
    /// </summary>
    internal static class Teleporting
    {
        /// <summary>
        /// Only first-person roles can be moved. For spectators, overwatch and similar, Position get
        /// returns Vector3.zero and the setter is a no-op, so a "successful" teleport would silently
        /// do nothing. Test the role, not the return value.
        /// </summary>
        public static bool CanMove(Player player)
            => player != null && !player.IsDestroyed && player.RoleBase is IFpcRole;

        public static bool To(Player player, Vector3 worldPosition)
        {
            if (!CanMove(player))
                return false;
            player.Position = worldPosition;
            return true;
        }

        /// <summary>
        /// Teleport to a spot stored as an offset inside a room (see RoomOps.ToLocal). The room is
        /// re-resolved each round, so this is seed-proof where a world coordinate is not.
        /// </summary>
        public static bool ToRoomOffset(Player player, Room room, Vector3 localOffset)
        {
            if (room == null || room.IsDestroyed)
                return false;
            return To(player, RoomOps.ToWorld(room, localOffset));
        }

        /// <summary>Relative step. Move adds a delta to the current position and sends one message.</summary>
        public static bool Nudge(Player player, Vector3 delta)
        {
            if (!CanMove(player))
                return false;
            player.Move(delta);
            return true;
        }

        /// <summary>
        /// Aim with LookRotation: X is the vertical angle (positive is up, the game clamps it to about
        /// -88..88), Y is the horizontal angle. Do not set Player.Rotation: it feeds the quaternion's
        /// Euler X straight in as the vertical angle, so pitch comes out inverted or clamped for most
        /// quaternions (inferred from code, not run).
        /// </summary>
        public static bool Face(Player player, float verticalDegrees, float horizontalDegrees)
        {
            if (!CanMove(player))
                return false;
            player.LookRotation = new Vector2(Mathf.Clamp(verticalDegrees, -88f, 88f), horizontalDegrees);
            return true;
        }
    }
}
