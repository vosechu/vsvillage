using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Unbreakable-by-survival scaffold. Cleared by build progression or builder cancellation.
public class BlockBuildingScaffold : Block
{
    public override bool OnBlockBrokenWith(IWorldAccessor world, Entity byEntity, ItemSlot itemslot, BlockSelection blockSel, float dropQuantityMultiplier = 1f)
    {
        if (byEntity is EntityPlayer ep && ep.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative)
        {
            return base.OnBlockBrokenWith(world, byEntity, itemslot, blockSel, dropQuantityMultiplier);
        }
        if (world.Side == EnumAppSide.Server && byEntity is EntityPlayer ep2)
        {
            (ep2.Player as Vintagestory.API.Server.IServerPlayer)?.SendIngameError(
                "vsvillage:scaffold-protected",
                Vintagestory.API.Config.Lang.Get("vsvillage:scaffold-protected"));
        }
        return false;
    }
}
