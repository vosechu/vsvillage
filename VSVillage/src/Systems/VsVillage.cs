using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace VsVillage;

public class VsVillage : ModSystem
{
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
		api.RegisterBlockClass("MayorWorkstation", typeof(BlockMayorWorkstation));
		AiTaskRegistry.Register<AiTaskVillagerMeleeAttack>("villagermeleeattack");
		AiTaskRegistry.Register<AiTaskVillagerSeekEntity>("villagerseekentity");
		AiTaskRegistry.Register<AiTaskVillagerSleep>("villagersleep");
		AiTaskRegistry.Register<AiTaskVillagerSocialize>("villagersocialize");
		AiTaskRegistry.Register<AiTaskVillagerGotoWork>("villagergotowork");
		AiTaskRegistry.Register<AiTaskVillagerGotoGatherspot>("villagergotogather");
		AiTaskRegistry.Register<AiTaskVillagerGotoMayor>("villagergotomayor");
		AiTaskRegistry.Register<AiTaskVillagerFillTrough>("villagerfilltrough");
		AiTaskRegistry.Register<AiTaskVillagerCultivateCrops>("villagercultivatecrops");
		AiTaskRegistry.Register<AiTaskVillagerFlipWeapon>("villagerflipweapon");
		AiTaskRegistry.Register<AiTaskStayCloseToEmployer>("villagerstayclose");
		AiTaskRegistry.Register<AiTaskHealWounded>("villagerhealwounded");
		AiTaskRegistry.Register<AiTaskVillagerRangedAttack>("villagerrangedattack");
		AiTaskRegistry.Register<AiTaskVillagerWander>("villagerwander");
		AiTaskRegistry.Register<AiTaskVillagerShepherdWander>("villagershepherdwander");
		AiTaskRegistry.Register<AiTaskVillagerWaterFarmland>("villagerwater");
		AiTaskRegistry.Register<AiTaskVillagerPatrol>("villagerpatrol");
		AiTaskRegistry.Register<AiTaskVillagerSmithIdle>("villagersmithidle");
		AiTaskRegistry.Register<AiTaskVillagerProfessionIdle>("villagerprofessionidle");
		ActivityModSystem.ActionTypes.TryAdd("GotoPointOfInterest", typeof(GotoPointOfInterestAction));
		ActivityModSystem.ActionTypes.TryAdd("Sleep", typeof(SleepAction));
		ActivityModSystem.ActionTypes.TryAdd("ToggleBrazierFire", typeof(ToggleBrazierFireAction));
		ActivityModSystem.ConditionTypes.TryAdd("CloseToPointOfInterest", typeof(CloseToPointOfInterestCondition));
		ActivityModSystem.ConditionTypes.TryAdd("CooldownCondition", typeof(CooldownCondition));
		ActivityModSystem.ActionTypes.TryAdd("CultivateCrops", typeof(CultivateCropsAction));
		ActivityModSystem.ActionTypes.TryAdd("FillTrough", typeof(FillTroughAction));
	}
}
