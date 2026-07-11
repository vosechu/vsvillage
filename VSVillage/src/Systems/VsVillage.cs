using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace VsVillage;

public class VsVillage : ModSystem
{
	// Process-wide container reservations shared by every transact/return-carry task, so two
	// villagers never target the same chest. Transient — empty after a restart, which is correct.
	public static readonly ContainerClaimRegistry ContainerClaims = new ContainerClaimRegistry();

	public override void Start(ICoreAPI api)
	{
		base.Start(api);
		api.RegisterEntityBehaviorClass("Villager", typeof(EntityBehaviorVillager));
		api.RegisterEntityBehaviorClass("replacewithentity", typeof(EntityBehaviorReplaceWithEntity));
		api.RegisterItemClass("ItemVillagerGear", typeof(ItemVillagerGear));
		api.RegisterItemClass("ItemVillagerHorn", typeof(ItemVillagerHorn));
		api.RegisterBlockEntityClass("VillagerBed", typeof(BlockEntityVillagerBed));
		api.RegisterBlockEntityClass("VillagerWorkstation", typeof(BlockEntityVillagerWorkstation));
		api.RegisterBlockEntityClass("VillagerWaypoint", typeof(BlockEntityVillagerWaypoint));
		api.RegisterBlockEntityClass("VillagerBrazier", typeof(BlockEntityVillagerBrazier));
		api.RegisterBlockClass("MayorWorkstation",  typeof(BlockMayorWorkstation));
		api.RegisterBlockClass("VsWorkstation",     typeof(BlockVsWorkstation));
		api.RegisterBlockClass("VsBed",             typeof(BlockVsBed));
		api.RegisterBlockClass("BuildingMarker",    typeof(BlockBuildingMarker));
		api.RegisterBlockClass("BuildingScaffold",  typeof(BlockBuildingScaffold));
		api.RegisterBlockEntityClass("BuildingMarker", typeof(BlockEntityBuildingMarker));
		api.RegisterEntity("EntityTravellingTrader", typeof(EntityTravellingTrader));
		api.RegisterEntity("EntityTravellingGuard", typeof(EntityTravellingGuard));
		api.RegisterEntityBehaviorClass("TravellingTrader", typeof(EntityBehaviorTravellingTrader));
		api.RegisterEntityBehaviorClass("TravellingGuard", typeof(EntityBehaviorTravellingGuard));
		AiTaskRegistry.Register<AiTaskVillagerBuild>("villagerbuild");
		AiTaskRegistry.Register<AiTaskVillagerBuilderMaintenance>("villagerbuildermaintenance");
		AiTaskRegistry.Register<AiTaskVillagerFleeEntity>("villagerflee");
		AiTaskRegistry.Register<AiTaskVillagerMeleeAttack>("villagermeleeattack");
		AiTaskRegistry.Register<AiTaskVillagerSeekEntity>("villagerseekentity");
		AiTaskRegistry.Register<AiTaskVillagerChaseEntity>("villagerchaseentity");
		AiTaskRegistry.Register<AiTaskVillagerSleep>("villagersleep");
		AiTaskRegistry.Register<AiTaskVillagerSocialize>("villagersocialize");
		AiTaskRegistry.Register<AiTaskVillagerAmbientChat>("villagerambientchat");
		AiTaskRegistry.Register<AiTaskVillagerGotoWork>("villagergotowork");
		AiTaskRegistry.Register<AiTaskVillagerGotoGatherspot>("villagergotogather");
		AiTaskRegistry.Register<AiTaskVillagerGotoMayor>("villagergotomayor");
		AiTaskRegistry.Register<AiTaskVillagerFillTrough>("villagerfilltrough");
		AiTaskRegistry.Register<AiTaskVillagerShepherdFetchFeed>("villagershepherdfetchfeed");
		AiTaskRegistry.Register<AiTaskGotoAndTransact>("villagergotoandtransact");
		AiTaskRegistry.Register<AiTaskVillagerReturnCarry>("villagerreturncarry");
		AiTaskRegistry.Register<AiTaskVillagerCultivateCrops>("villagercultivatecrops");
		AiTaskRegistry.Register<AiTaskVillagerRemoveDeadCrops>("villagerremovedeadcrops");
		AiTaskRegistry.Register<AiTaskVillagerFlipWeapon>("villagerflipweapon");
		AiTaskRegistry.Register<AiTaskStayCloseToEmployer>("villagerstayclose");
		AiTaskRegistry.Register<AiTaskHealWounded>("villagerhealwounded");
		AiTaskRegistry.Register<AiTaskVillagerRangedAttack>("villagerrangedattack");
		AiTaskRegistry.Register<AiTaskVillagerWander>("villagerwander");
		AiTaskRegistry.Register<AiTaskVillagerWaterFarmland>("villagerwater");
		AiTaskRegistry.Register<AiTaskVillagerHoeTilling>("villagerhoetilling");
		AiTaskRegistry.Register<AiTaskVillagerShepherdTend>("villagershepherdtend");
		AiTaskRegistry.Register<AiTaskVillagerProfessionChat>("villagerprofessionchat");
		AiTaskRegistry.Register<AiTaskVillagerPatrol>("villagerpatrol");
		AiTaskRegistry.Register<AiTaskVillagerSmithIdle>("villagersmithidle");
		AiTaskRegistry.Register<AiTaskVillagerSmithHammer>("villagersmithhammer");
		AiTaskRegistry.Register<AiTaskVillagerProfessionIdle>("villagerprofessionidle");
		AiTaskRegistry.Register<AiTaskVillagerStormShelter>("villagerstormshelter");
		AiTaskRegistry.Register<AiTaskVillagerGather>("villagergather");
		AiTaskRegistry.Register<AiTaskVillagerTameAnimal>("villagertameanimal");
		AiTaskRegistry.Register<AiTaskVillagerCheckFlowers>("villagercheckflowers");
		AiTaskRegistry.Register<AiTaskVillagerFillFlowerpot>("villagerfillflowerpot");
		AiTaskRegistry.Register<AiTaskVillagerBakerCollectBread>("villagerbakercollectbread");
		AiTaskRegistry.Register<AiTaskVillagerBakerTendOven>("villagerbakertendoven");
		AiTaskRegistry.Register<AiTaskVillagerFarmerHelp>("villagerfarmerhelp");
		AiTaskRegistry.Register<AiTaskTravellingTraderStand>("travellingtraderstand");
		AiTaskRegistry.Register<AiTaskTravellingTraderLeave>("travellingtraderleave");
		AiTaskRegistry.Register<AiTaskTravellingGuardFollow>("travellingguardfollow");
		AiTaskRegistry.Register<AiTaskTravellingGuardStand>("travellingguardstand");
		AiTaskRegistry.Register<AiTaskMechHelperReturnToBase>("mechhelpreturntobase");
		ActivityModSystem.ActionTypes.TryAdd("GotoPointOfInterest", typeof(GotoPointOfInterestAction));
		ActivityModSystem.ActionTypes.TryAdd("Sleep", typeof(SleepAction));
		ActivityModSystem.ActionTypes.TryAdd("ToggleBrazierFire", typeof(ToggleBrazierFireAction));
		ActivityModSystem.ConditionTypes.TryAdd("CloseToPointOfInterest", typeof(CloseToPointOfInterestCondition));
		ActivityModSystem.ConditionTypes.TryAdd("CooldownCondition", typeof(CooldownCondition));
		ActivityModSystem.ActionTypes.TryAdd("CultivateCrops", typeof(CultivateCropsAction));
		ActivityModSystem.ActionTypes.TryAdd("FillTrough", typeof(FillTroughAction));
	}
}
