namespace VsVillage;

public class VillageConfig
{
	public float VillageChance = 0.05f;

	public int WorldgenBufferBlocks = 20;

	// When true, chiseled and microblocks count as room walls. Lets builders use
	// chiseled walls for villager rooms without failing vanilla room checks.
	public bool ChiseledBlocksCountAsWalls = true;
}
