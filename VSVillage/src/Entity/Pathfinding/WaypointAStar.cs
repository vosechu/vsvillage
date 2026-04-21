using Vintagestory.API.Common;

namespace VsVillage;

public class WaypointAStar : VillagerAStarNew
{
	public WaypointAStar(ICachingBlockAccessor blockAccessor)
		: base(blockAccessor)
	{
	}

	protected bool canStep(Block belowBlock)
	{
		return steppableCodes.Exists((string code) => belowBlock.Code.Path.Contains(code));
	}
}
