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
	recoverOrphanedVillagers,   // reassign stale-VillageId villagers within village radius
	dismissMechhelper,          // despawn the Settlement Keeper bound to this village
	recoverFixtures,            // re-register beds/workstations/braziers/waypoints within village radius
	assignGuardPost,            // player assigns a villager to a guard post's day or night watch
	changeShopType              // player pays to re-specialise a trader workstation's tradelist
}
