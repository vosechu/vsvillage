using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VsVillage;

[JsonObject(MemberSerialization.OptIn)]
public class GotoPointOfInterestAction : EntityActionBase
{
	public const string ActionType = "GotoPointOfInterest";

	[JsonProperty]
	public VillagePointOfInterest Target;

	[JsonProperty]
	public float AnimSpeed = 1f;

	[JsonProperty]
	public float WalkSpeed = 0.02f;

	[JsonProperty]
	public string AnimCode = "walk";

	private bool isFinished = true;

	private List<VillagerPathNode> path;

	private int index;

	public override string Type => "GotoPointOfInterest";

	public override void OnTick(float dt)
	{
		EntityPos serverPos = ((Entity)vas.Entity).ServerPos;
		int num = index;
		List<VillagerPathNode> list = path;
		if (!(num < ((list != null) ? new int?(list.Count - 1) : ((int?)null))))
		{
			return;
		}
		float num2 = path[index].BlockPos.DistanceSqTo(serverPos.X, serverPos.Y, serverPos.Z);
		if (num2 > 1f)
		{
			for (int i = index; i < path.Count; i++)
			{
				if (path[i].BlockPos.DistanceSqTo(serverPos.X, serverPos.Y, serverPos.Z) < num2)
				{
					index = i;
					break;
				}
			}
		}
		if (path.Count >= index && index > 0 && path[index - 1].IsDoor)
		{
			toggleDoor(opened: false, path[index - 1].BlockPos);
		}
		if (path.Count > index && path[index].IsDoor)
		{
			toggleDoor(opened: true, path[index].BlockPos);
		}
		if (path.Count > index + 1 && path[index + 1].IsDoor)
		{
			toggleDoor(opened: true, path[index + 1].BlockPos);
		}
	}

	public override void Start(EntityActivity entityActivity)
	{
		EntityBehaviorVillager behavior = vas.Entity.GetBehavior<EntityBehaviorVillager>();
		Village village = behavior?.Village;
		if (village != null)
		{
			BlockPos asBlockPos = ((Entity)vas.Entity).ServerPos.AsBlockPos;
			BlockPos end = Target switch
			{
				VillagePointOfInterest.workstation => behavior.Workstation, 
				VillagePointOfInterest.bed => behavior.Bed, 
				VillagePointOfInterest.gatherplace => (village.Gatherplaces.Count > 0) ? village.Gatherplaces.ElementAt(behavior.entity.World.Rand.Next() % village.Gatherplaces.Count) : null, 
				_ => null, 
			};
			index = 0;
			path = behavior.Pathfind.FindPath(asBlockPos, end, behavior.Village);
			if (path != null)
			{
				isFinished = false;
				vas.Entity.AnimManager.StartAnimation(new AnimationMetaData
				{
					Animation = AnimCode,
					Code = AnimCode,
					AnimationSpeed = AnimSpeed,
					BlendMode = EnumAnimationBlendMode.Average
				}.Init());
				vas.wppathTraverser.FollowRoute(behavior.Pathfind.ToWaypoints(path), WalkSpeed, 0.2f, delegate
				{
					isFinished = true;
				}, delegate
				{
					isFinished = true;
				});
			}
		}
		base.Start(entityActivity);
	}

	public override void Pause(EnumInteruptionType interuptionType)
	{
		Finish();
	}

	public override void Resume()
	{
		Start(null);
	}

	public override bool IsFinished()
	{
		return isFinished;
	}

	public override void Finish()
	{
		isFinished = true;
		vas.linepathTraverser.Stop();
		vas.wppathTraverser.Stop();
		vas.Entity.AnimManager.StopAnimation(AnimCode);
		vas.Entity.Controls.StopAllMovement();
	}

	public override IEntityAction Clone()
	{
		return new GotoPointOfInterestAction
		{
			vas = vas,
			Target = Target,
			AnimCode = AnimCode,
			AnimSpeed = AnimSpeed,
			WalkSpeed = WalkSpeed
		};
	}

	public override void AddGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		ElementBounds elementBounds = ElementBounds.Fixed(0.0, 0.0, 200.0, 25.0);
		ElementBounds elementBounds2 = elementBounds.BelowCopy();
		ElementBounds elementBounds3 = elementBounds2.BelowCopy();
		ElementBounds elementBounds4 = elementBounds3.BelowCopy();
		string[] array = new List<VillagePointOfInterest>(Enum.GetValues<VillagePointOfInterest>()).ConvertAll((VillagePointOfInterest poi) => poi.ToString()).ToArray();
		singleComposer.AddStaticText("Point of Interest", CairoFont.WhiteDetailText(), elementBounds).AddDropDown(array, array, 0, delegate
		{
		}, elementBounds.RightCopy(), "Target").AddStaticText("Walkspeed", CairoFont.WhiteDetailText(), elementBounds2)
			.AddNumberInput(elementBounds2.RightCopy(), delegate
			{
			}, null, "Walkspeed")
			.AddStaticText("Animation", CairoFont.WhiteDetailText(), elementBounds3)
			.AddTextInput(elementBounds3.RightCopy(), delegate
			{
			}, null, "Animation")
			.AddStaticText("Animationspeed", CairoFont.WhiteDetailText(), elementBounds4)
			.AddNumberInput(elementBounds4.RightCopy(), delegate
			{
			}, null, "Animationspeed");
		singleComposer.GetDropDown("Target").SetSelectedIndex((int)Target);
		singleComposer.GetNumberInput("Walkspeed").SetValue(WalkSpeed);
		singleComposer.GetTextInput("Animation").SetValue(AnimCode);
		singleComposer.GetNumberInput("Animationspeed").SetValue(AnimSpeed);
	}

	public override bool StoreGuiEditFields(ICoreClientAPI capi, GuiComposer singleComposer)
	{
		Target = Enum.Parse<VillagePointOfInterest>(singleComposer.GetDropDown("Target").SelectedValue);
		WalkSpeed = singleComposer.GetNumberInput("Walkspeed").GetValue();
		AnimCode = singleComposer.GetTextInput("Animation").GetText();
		AnimSpeed = singleComposer.GetNumberInput("Animationspeed").GetValue();
		return true;
	}

	public override string ToString()
	{
		return $"Goto {Target}, Walkspeed {WalkSpeed}, Animation {AnimCode}, AnimSpeed {AnimSpeed}";
	}

	private void toggleDoor(bool opened, BlockPos target)
	{
		Block block = vas.Entity.Api.World.BlockAccessor.GetBlock(target);
		BlockSelection blockSel = new BlockSelection
		{
			Block = block,
			Position = target,
			HitPosition = new Vec3d(0.5, 0.5, 0.5),
			Face = BlockFacing.NORTH
		};
		TreeAttribute treeAttribute = new TreeAttribute();
		treeAttribute.SetBool("opened", opened);
		block.Activate(vas.Entity.World, new Caller
		{
			Entity = vas.Entity,
			Type = EnumCallerType.Entity,
			Pos = ((Entity)vas.Entity).Pos.XYZ
		}, blockSel, treeAttribute);
	}
}
