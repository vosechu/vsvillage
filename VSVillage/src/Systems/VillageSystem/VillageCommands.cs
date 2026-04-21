using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VsVillage;

public class VillageCommands : ModSystem
{
	private BlockPos start;

	private BlockPos end;

	private ICoreServerAPI sapi;

	private bool revertHighlightPaths = true;

	private bool revertHighlightVillage = true;

	private bool revertHighlightVillagerBelongings = true;

	public override bool ShouldLoad(EnumAppSide forSide)
	{
		return forSide == EnumAppSide.Server;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		base.StartServerSide(api);
		sapi = api;
		IChatCommandApi chatCommands = sapi.ChatCommands;
		CommandArgumentParsers parsers = chatCommands.Parsers;
		chatCommands.Create("villagerpath").WithAlias("vp").WithDescription("A* path finding debug testing for villagers")
			.WithArgs(parsers.WordRange("stage", "start", "end"))
			.RequiresPrivilege(Privilege.root)
			.WithExamples("villagerpath start", "villagerpath end")
			.HandleWith((TextCommandCallingArgs args) => onCmdAStar(args));
		chatCommands.Create("waypointpath").WithAlias("wp").WithDescription("A* path finding debug testing for villagers")
			.WithArgs(parsers.WordRange("stage", "start", "end"))
			.RequiresPrivilege(Privilege.root)
			.WithExamples("waypointpath start", "waypointpath end")
			.HandleWith((TextCommandCallingArgs args) => onCmdAStar(args, new WaypointAStar(sapi.World.GetCachingBlockAccessor(synchronize: true, relight: true))));
		chatCommands.Create("highlightvillagewaypoints").WithAlias("hvw").WithDescription("Highlight all paths between waypoints in your village")
			.RequiresPrivilege(Privilege.root)
			.WithExamples("highlightvillagewaypoints", "hvw")
			.HandleWith(onCmdHighlightWaypoints);
		chatCommands.Create("highlightvillageplaces").WithAlias("hvp").WithDescription("Highlight this village center blue, the village border red, the beds yellow, the workstations green, the gather places purple and the waypoints turquoise")
			.RequiresPrivilege(Privilege.root)
			.WithExamples("highlightvillageplaces", "hvp")
			.HandleWith(onCmdHighlightPlaces);
		chatCommands.Create("highlightvillagersbelongings").WithAlias("hvb").WithDescription("Gets the closest villager/bed/workstation and highlight the villagers posistion red, its beds blue and workstation green")
			.RequiresPrivilege(Privilege.root)
			.WithExamples("highlightvillagersbelongings", "hvb")
			.HandleWith(onCmdHighlightVillagerBelongings);
	}

	private TextCommandResult onCmdHighlightVillagerBelongings(TextCommandCallingArgs args)
	{
		IPlayer player = args.Caller.Player;
		revertHighlightVillagerBelongings = !revertHighlightVillagerBelongings;
		if (revertHighlightVillagerBelongings)
		{
			sapi.World.HighlightBlocks(player, 1, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(0, 0, 128, 100) });
			sapi.World.HighlightBlocks(player, 2, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(128, 128, 0, 100) });
			sapi.World.HighlightBlocks(player, 3, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(128, 0, 0, 100) });
			return TextCommandResult.Success("Highlighted villager/bed/workstation been unhighlighted");
		}
		BlockPos plrPos = ((Entity)player.Entity).ServerPos.XYZ.AsBlockPos;
		Village village = sapi.ModLoader.GetModSystem<VillageManager>().GetVillage(plrPos);
		if (village == null)
		{
			return TextCommandResult.Error("No village found");
		}
		Entity closestVillager = sapi.World.GetNearestEntity(plrPos.ToVec3d(), 10f, 10f, (Entity candidate) => candidate is EntityVillager);
		VillagerWorkstation villagerWorkstation = village.Workstations.Values.MinBy((VillagerWorkstation candidate) => candidate.Pos.DistanceTo(plrPos));
		VillagerBed villagerBed = village.Beds.Values.MinBy((VillagerBed candidate) => candidate.Pos.DistanceTo(plrPos));
		float num = ((closestVillager != null) ? closestVillager.Pos.AsBlockPos.DistanceTo(plrPos) : float.MaxValue);
		float num2 = villagerWorkstation?.Pos.DistanceTo(plrPos) ?? float.MaxValue;
		float num3 = villagerBed?.Pos.DistanceTo(plrPos) ?? float.MaxValue;
		if (num < num3 && num < num2)
		{
			villagerWorkstation = village.Workstations.Values.FirstOrDefault((VillagerWorkstation candidate) => candidate.OwnerId == closestVillager.EntityId);
			villagerBed = village.Beds.Values.FirstOrDefault((VillagerBed candidate) => candidate.OwnerId == closestVillager.EntityId);
		}
		else if (num2 < num3 && num2 < num)
		{
			closestVillager = sapi.World.GetEntityById(villagerWorkstation.OwnerId) as EntityVillager;
			villagerBed = village.Beds.Values.FirstOrDefault((VillagerBed candidate) => candidate.OwnerId == closestVillager?.EntityId);
		}
		else
		{
			if (!(num3 < num2) || !(num3 < num))
			{
				return TextCommandResult.Error("No villager/bed/workstation could be found closeby");
			}
			closestVillager = sapi.World.GetEntityById(villagerBed.OwnerId) as EntityVillager;
			villagerWorkstation = village.Workstations.Values.FirstOrDefault((VillagerWorkstation candidate) => candidate.OwnerId == closestVillager?.EntityId);
		}
		village.Pos.Copy().Y = sapi.World.BlockAccessor.GetTerrainMapheightAt(village.Pos);
		List<BlockPos> blocks = ((villagerWorkstation != null) ? addBlockHeight(new List<BlockPos> { villagerWorkstation.Pos }, 20) : new List<BlockPos>());
		List<BlockPos> blocks2 = ((villagerBed != null) ? addBlockHeight(new List<BlockPos> { villagerBed.Pos }, 20) : new List<BlockPos>());
		List<BlockPos> list = ((closestVillager != null) ? addBlockHeight(new List<BlockPos> { closestVillager.Pos.AsBlockPos }, 20) : new List<BlockPos>());
		list = addBlockHeight(list);
		sapi.World.HighlightBlocks(player, 1, blocks, new List<int> { ColorUtil.ColorFromRgba(0, 0, 128, 100) });
		sapi.World.HighlightBlocks(player, 2, blocks2, new List<int> { ColorUtil.ColorFromRgba(128, 128, 0, 100) });
		sapi.World.HighlightBlocks(player, 3, list, new List<int> { ColorUtil.ColorFromRgba(128, 0, 0, 100) });
		return TextCommandResult.Success("A villager together with is belonging bed and workstation have been highlighted.");
	}

	private TextCommandResult onCmdHighlightPlaces(TextCommandCallingArgs args)
	{
		IPlayer player = args.Caller.Player;
		revertHighlightVillage = !revertHighlightVillage;
		if (revertHighlightVillage)
		{
			sapi.World.HighlightBlocks(player, 4, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(0, 0, 128, 100) });
			sapi.World.HighlightBlocks(player, 5, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(0, 128, 0, 100) });
			sapi.World.HighlightBlocks(player, 6, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(128, 128, 0, 100) });
			sapi.World.HighlightBlocks(player, 7, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(128, 0, 128, 100) });
			sapi.World.HighlightBlocks(player, 8, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(0, 128, 128, 100) });
			sapi.World.HighlightBlocks(player, 9, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(128, 0, 0, 100) });
			return TextCommandResult.Success("Highlighted Points of interest in this village have been unhighlighted");
		}
		BlockPos asBlockPos = ((Entity)player.Entity).ServerPos.XYZ.AsBlockPos;
		Village village = sapi.ModLoader.GetModSystem<VillageManager>().GetVillage(asBlockPos);
		if (village == null)
		{
			return TextCommandResult.Error("No village found");
		}
		BlockPos blockPos = village.Pos.Copy();
		blockPos.Y = sapi.World.BlockAccessor.GetTerrainMapheightAt(village.Pos);
		List<BlockPos> blocks = addBlockHeight(new List<BlockPos> { blockPos }, 10);
		List<BlockPos> blocks2 = addBlockHeight(village.Workstations.Keys.ToList(), 10);
		List<BlockPos> blocks3 = addBlockHeight(village.Beds.Keys.ToList(), 10);
		List<BlockPos> blocks4 = addBlockHeight(village.Gatherplaces.ToList(), 10);
		List<BlockPos> blocks5 = addBlockHeight(village.Waypoints.ToList());
		List<BlockPos> list = addBlockHeight(new List<BlockPos>());
		for (int i = -village.Radius; i <= village.Radius; i++)
		{
			list.Add(blockPos.AddCopy(i, 0, -village.Radius));
			list.Add(blockPos.AddCopy(i, 0, village.Radius));
		}
		for (int j = -village.Radius + 1; j < village.Radius; j++)
		{
			list.Add(blockPos.AddCopy(-village.Radius, 0, j));
			list.Add(blockPos.AddCopy(village.Radius, 0, j));
		}
		list = addBlockHeight(list);
		sapi.World.HighlightBlocks(player, 4, blocks, new List<int> { ColorUtil.ColorFromRgba(0, 0, 128, 100) });
		sapi.World.HighlightBlocks(player, 5, blocks2, new List<int> { ColorUtil.ColorFromRgba(0, 128, 0, 100) });
		sapi.World.HighlightBlocks(player, 6, blocks3, new List<int> { ColorUtil.ColorFromRgba(128, 128, 0, 100) });
		sapi.World.HighlightBlocks(player, 7, blocks4, new List<int> { ColorUtil.ColorFromRgba(128, 0, 128, 100) });
		sapi.World.HighlightBlocks(player, 8, blocks5, new List<int> { ColorUtil.ColorFromRgba(0, 128, 128, 100) });
		sapi.World.HighlightBlocks(player, 9, list, new List<int> { ColorUtil.ColorFromRgba(128, 0, 0, 100) });
		return TextCommandResult.Success("All Points of interest in this village have been highlighted.");
	}

	private List<BlockPos> addBlockHeight(List<BlockPos> list, int height = 5)
	{
		List<BlockPos> list2 = new List<BlockPos>();
		foreach (BlockPos item in list)
		{
			for (int i = 0; i < height; i++)
			{
				list2.Add(item.UpCopy(i));
			}
		}
		return list2;
	}

	private TextCommandResult onCmdHighlightWaypoints(TextCommandCallingArgs args)
	{
		IPlayer player = args.Caller.Player;
		revertHighlightPaths = !revertHighlightPaths;
		if (revertHighlightPaths)
		{
			sapi.World.HighlightBlocks(player, 3, new List<BlockPos>(), new List<int> { ColorUtil.ColorFromRgba(128, 0, 0, 100) });
			return TextCommandResult.Success("Highlighted paths have been unhighlighted");
		}
		new WaypointAStar(sapi.World.GetCachingBlockAccessor(synchronize: true, relight: true));
		BlockPos asBlockPos = ((Entity)player.Entity).ServerPos.XYZ.AsBlockPos;
		HashSet<BlockPos> collection = new HashSet<BlockPos>();
		if (sapi.ModLoader.GetModSystem<VillageManager>().GetVillage(asBlockPos) == null)
		{
			return TextCommandResult.Error("No village found");
		}
		sapi.World.HighlightBlocks(player, 3, new List<BlockPos>(collection), new List<int> { ColorUtil.ColorFromRgba(128, 0, 0, 100) });
		return TextCommandResult.Success("All paths have been highlighted");
	}

	private TextCommandResult onCmdAStar(TextCommandCallingArgs args, WaypointAStar waypointAStar = null)
	{
		string text = (string)args[0];
		IPlayer player = args.Caller.Player;
		BlockPos asBlockPos = ((Entity)player.Entity).ServerPos.XYZ.AsBlockPos;
		VillagerAStarNew villagerAStarNew = waypointAStar ?? new VillagerAStarNew(sapi.World.GetCachingBlockAccessor(synchronize: true, relight: true));
		new Cuboidf(-0.4f, 0f, -0.4f, 0.4f, 1.5f, 0.4f);
		new Cuboidf(-0.2f, 0f, -0.2f, 0.2f, 1.5f, 0.2f);
		new Cuboidf(-0.6f, 0f, -0.6f, 0.6f, 1.5f, 0.6f);
		switch (text)
		{
		case "start":
			start = villagerAStarNew.GetStartPos(((Entity)player.Entity).ServerPos.XYZ);
			sapi.World.HighlightBlocks(player, 26, new List<BlockPos> { start }, new List<int> { ColorUtil.ColorFromRgba(255, 255, 0, 128) });
			break;
		case "end":
			end = asBlockPos.Copy();
			sapi.World.HighlightBlocks(player, 27, new List<BlockPos> { end }, new List<int> { ColorUtil.ColorFromRgba(255, 0, 255, 128) });
			break;
		case "bench":
		{
			if (start == null || end == null)
			{
				return TextCommandResult.Deferred;
			}
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
			for (int i = 0; i < 15; i++)
			{
				villagerAStarNew.FindPath(start, end);
			}
			stopwatch.Stop();
			float num = (float)stopwatch.ElapsedMilliseconds / 15f;
			return TextCommandResult.Success($"15 searches average: {(int)num} ms");
		}
		case "clear":
			start = null;
			end = null;
			sapi.World.HighlightBlocks(player, 2, new List<BlockPos>());
			sapi.World.HighlightBlocks(player, 26, new List<BlockPos>());
			sapi.World.HighlightBlocks(player, 27, new List<BlockPos>());
			break;
		}
		if (start == null || end == null)
		{
			sapi.World.HighlightBlocks(player, 2, new List<BlockPos>());
		}
		if (start != null && end != null)
		{
			Stopwatch stopwatch2 = new Stopwatch();
			stopwatch2.Start();
			List<VillagerPathNode> list = villagerAStarNew.FindPath(start, end);
			stopwatch2.Stop();
			int num2 = (int)stopwatch2.ElapsedMilliseconds;
			if (list == null)
			{
				sapi.World.HighlightBlocks(player, 2, new List<BlockPos>());
				sapi.World.HighlightBlocks(player, 3, new List<BlockPos>());
				return TextCommandResult.Error("No path found");
			}
			List<BlockPos> list2 = new List<BlockPos>();
			foreach (VillagerPathNode item in list)
			{
				list2.Add(item.BlockPos);
			}
			sapi.World.HighlightBlocks(player, 2, list2, new List<int> { ColorUtil.ColorFromRgba(128, 128, 128, 30) });
			List<Vec3d> list3 = list.ConvertAll((VillagerPathNode node) => node.BlockPos.ToVec3d());
			list2 = new List<BlockPos>();
			foreach (Vec3d item2 in list3)
			{
				list2.Add(item2.AsBlockPos);
			}
			sapi.World.HighlightBlocks(player, 3, list2, new List<int> { ColorUtil.ColorFromRgba(128, 0, 0, 100) });
			return TextCommandResult.Success($"Search took {num2} ms, {0} nodes checked");
		}
		return TextCommandResult.Deferred;
	}
}
