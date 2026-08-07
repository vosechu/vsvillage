using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace VsVillage;

// Ambient village chat goes to the vanilla info-log tab so it stays out of general chat.
// The client hardcodes that tab (HudDialogChat builds chat-group--6 unconditionally), so it
// needs no group registration or membership and works the same in single player.
public static class VillageChat
{
    // Village-local events: only players actually inside the village hear them.
    public static void SendToVillage(ICoreServerAPI sapi, Village village, string message)
    {
        if (sapi == null || village == null || string.IsNullOrEmpty(message)) return;

        IPlayer[] all = sapi.World.AllOnlinePlayers;
        for (int i = 0; i < all.Length; i++)
        {
            IServerPlayer sp = all[i] as IServerPlayer;
            if (sp?.Entity == null) continue;
            if (village.IsInside(sp.Entity.Pos.X, sp.Entity.Pos.Z))
                sp.SendMessage(GlobalConstants.InfoLogChatGroup, message, EnumChatType.Notification);
        }
    }

    // Direct feedback for something this player just did (button, item use, admin op).
    public static void SendToPlayer(IServerPlayer player, string message)
    {
        if (player == null || string.IsNullOrEmpty(message)) return;
        player.SendMessage(GlobalConstants.InfoLogChatGroup, message, EnumChatType.Notification);
    }
}
