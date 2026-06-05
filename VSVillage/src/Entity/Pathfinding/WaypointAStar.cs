using Vintagestory.API.Common;

namespace VsVillage;

public class WaypointAStar : VillagerAStarNew
{
	public WaypointAStar(ICachingBlockAccessor blockAccessor, IWorldAccessor world)
		: base(blockAccessor, world)
	{
	}

	protected bool canStep(Block belowBlock)
	{
		return steppableCodes.Exists((string code) => belowBlock.Code.Path.Contains(code));
	}
}
