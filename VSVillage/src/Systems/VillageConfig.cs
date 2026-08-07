namespace VsVillage;

public class VillageConfig
{
	public float VillageChance = 0.05f;

	public int WorldgenBufferBlocks = 20;

	// When true, chiseled and microblocks count as room walls. Lets builders use
	// chiseled walls for villager rooms without failing vanilla room checks.
	public bool ChiseledBlocksCountAsWalls = true;

	// Broadcast travelling-trader arriving/departing messages to all players. False silences them.
	public bool ShowTravellingTraderMessages = true;

	// Broadcast village guard "spotted enemy" contact reports to nearby players. False silences them.
	public bool ShowGuardAlertMessages = true;

	// Announce shepherd tamings to nearby players. False silences them.
	public bool ShowTamingMessages = true;
}
