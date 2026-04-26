namespace VsVillage;

public enum EnumVillageManagementOperation
{
	create,
	destroy,
	removeVillager,
	removeStructure,
	changeStats,
	hireVillager,
	gatherVillagers,
	clearGather,
	validateStructures,
	markStructureInvalid,  // force-remove a ghost entry even when no block entity exists
	assignWorkstation,          // player assigns a villager to a specific workstation
	assignBed,                  // player assigns a villager to a specific bed
	recoverOrphanedVillagers    // reassign stale-VillageId villagers within village radius
}
