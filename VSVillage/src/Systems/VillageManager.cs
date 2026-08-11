using ProtoBuf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VsVillage;

public class VillageManager : ModSystem
{
	public ConcurrentDictionary<string, Village> Villages = new ConcurrentDictionary<string, Village>();

	private ICoreAPI Api;

	private const int villagerHiringCost = 5;

	// When true, AiTaskVillagerStormShelter treats conditions as if
	// a temporal storm is active regardless of the real stability system.
	// Set via /vsvillage stormdrillstart and cleared by /vsvillage stormdrillend.
	public static bool StormDrillActive { get; set; }

	public override void Start(ICoreAPI api)
	{
		base.Start(api);
		Api = api;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		base.StartServerSide(api);
		api.Event.GameWorldSave += delegate
		{
			OnSave(api);
		};
		api.Network.RegisterChannel("villagemanagementnetwork")
			.RegisterMessageType<Village>()
			.RegisterMessageType<VillageManagementMessage>()
			.RegisterMessageType<VillageAssignmentContext>()
			.SetMessageHandler(delegate(IServerPlayer fromPlayer, VillageManagementMessage message)
			{
				OnManagementMessage(fromPlayer, message, api);
			});
		RegisterAdminCommands(api);
		api.Event.OnTestBlockAccess += OnTestBlockAccess;
		api.Event.SaveGameLoaded += delegate
		{
			LoadBackfilledClaims(api);
		};
		// Subscribed unconditionally: VillageGenerator owns the config and starts after this
		// ModSystem, so the opt-in is read inside the handler, not here.
		api.Event.MapRegionLoaded += delegate(Vec2i mapCoord, IMapRegion region)
		{
			ClaimGeneratedVillages(api, region);
		};
	}

	// Marks a claim as ours. LandClaim.OwnedByEntityId is persisted and copied by Clone but is
	// read nowhere in the engine - not even TestPlayerAccess - so it identifies our claims without
	// needing the village loaded, which for a worldgen village only happens in its gen session.
	private const long VillageClaimTag = -8265L;

	private const string BackfilledClaimsKey = "vsvillage-backfilledclaims";

	// Footprints the backfill has already claimed once. A footprint is never claimed twice, so an
	// admin removing a claim by any means keeps it removed instead of it returning on region load.
	private readonly HashSet<long> backfilledVillageClaims = new HashSet<long>();

	// Worldgen village claims set AllowUseEveryone so players can walk in and trade, but vanilla
	// only reads that flag in GetBlockingLandClaimant - TestPlayerAccess still reports Denied, so
	// looting the generated chests/toolracks and using the villagers' own fixtures stays blocked.
	private EnumWorldAccessResponse OnTestBlockAccess(
		IPlayer player, BlockSelection blockSel, EnumBlockAccessFlags accessType,
		ref string claimant, EnumWorldAccessResponse response)
	{
		if (accessType != EnumBlockAccessFlags.Use) return response;
		if (player == null || blockSel?.Position == null) return response;
		// Same bypass vanilla applies before its own claim check, so this hook can't
		// re-deny an admin the base game already let through.
		if (player.HasPrivilege(Privilege.useblockseverywhere)
		    && player.WorldData.CurrentGameMode == EnumGameMode.Creative) return response;

		Block block = Api.World.BlockAccessor.GetBlock(blockSel.Position);
		bool isGated = block is BlockMayorWorkstation || block is BlockVsWorkstation || block is BlockVsBed;
		if (!isGated)
		{
			BlockEntity be = Api.World.BlockAccessor.GetBlockEntity(blockSel.Position);
			isGated = be is IBlockEntityContainer || be is BlockEntityToolrack;
		}
		if (!isGated) return response;

		LandClaim[] claims = Api.World.Claims.Get(blockSel.Position);
		if (claims == null || claims.Length == 0) return response;

		LandClaim blocking = null;
		foreach (LandClaim claim in claims)
		{
			if (claim.OwnedByEntityId != VillageClaimTag) continue;
			if (claim.TestPlayerAccess(player, EnumBlockAccessFlags.Use) != EnumPlayerAccessResult.Denied) return response;
			blocking = claim;
		}
		if (blocking == null) return response;

		claimant = blocking.LastKnownOwnerName ?? blocking.Description ?? "Village";
		return EnumWorldAccessResponse.LandClaimed;
	}

	// Opt-in and off by default: on an established world this locks players out of bases already
	// built inside a generated village. Each footprint is claimed at most once ever, so a removal
	// is not undone by the next region load.
	private void ClaimGeneratedVillages(ICoreServerAPI sapi, IMapRegion region)
	{
		if (region?.GeneratedStructures == null) return;
		if (!(sapi.ModLoader.GetModSystem<VillageGenerator>()?.Config?.BackfillClaimsOnExistingVillages ?? false)) return;

		foreach (GeneratedStructure gs in region.GeneratedStructures)
		{
			if (gs.Group != "village" || gs.Location == null) continue;
			if (!backfilledVillageClaims.Add(ClaimKey(gs.Location.MinX, gs.Location.MinZ))) continue;
			// The structure record has no name, so a backfilled claim falls back to a generic label.
			RegisterVillageClaim(sapi, gs.Location.MinX, gs.Location.MinZ, gs.Location.MaxX, gs.Location.MaxZ,
				Lang.Get("vsvillage:landclaim-village"));
		}
	}

	public void RegisterVillageClaim(ICoreServerAPI sapi, Village village)
	{
		if (village?.ClaimStart == null || village.ClaimEnd == null) return;
		RegisterVillageClaim(sapi, village.ClaimStart.X, village.ClaimStart.Z, village.ClaimEnd.X, village.ClaimEnd.Z, village.Name);
	}

	private static void RegisterVillageClaim(ICoreServerAPI sapi, int x1, int z1, int x2, int z2, string name)
	{
		int minX = Math.Min(x1, x2), maxX = Math.Max(x1, x2);
		int minZ = Math.Min(z1, z2), maxZ = Math.Max(z1, z2);
		if (FindVillageClaim(sapi, minX, minZ, maxX, maxZ) != null) return;

		sapi.World.Claims.Add(new LandClaim
		{
			Areas = new List<Cuboidi> { new Cuboidi(minX, 0, minZ, maxX, sapi.WorldManager.MapSizeY, maxZ) },
			Description = name,
			LastKnownOwnerName = name,
			OwnedByEntityId = VillageClaimTag,
			ProtectionLevel = 10,
			AllowUseEveryone = true,
			AllowTraverseEveryone = true
		});
	}

	public bool UnregisterVillageClaim(ICoreServerAPI sapi, Village village)
	{
		if (village?.ClaimStart == null || village.ClaimEnd == null) return false;

		int minX = Math.Min(village.ClaimStart.X, village.ClaimEnd.X);
		int maxX = Math.Max(village.ClaimStart.X, village.ClaimEnd.X);
		int minZ = Math.Min(village.ClaimStart.Z, village.ClaimEnd.Z);
		int maxZ = Math.Max(village.ClaimStart.Z, village.ClaimEnd.Z);

		LandClaim claim = FindVillageClaim(sapi, minX, minZ, maxX, maxZ);
		if (claim == null) return false;
		return sapi.World.Claims.Remove(claim);
	}

	// Requires both our tag and the exact box, so an unrelated claim overlapping the village
	// centre is never picked up.
	private static LandClaim FindVillageClaim(ICoreServerAPI sapi, int minX, int minZ, int maxX, int maxZ)
	{
		LandClaim[] claims = sapi.World.Claims.Get(new BlockPos((minX + maxX) / 2, 0, (minZ + maxZ) / 2));
		if (claims == null) return null;

		foreach (LandClaim claim in claims)
		{
			if (claim.OwnedByEntityId != VillageClaimTag) continue;
			foreach (Cuboidi claimed in claim.Areas)
			{
				if (claimed.MinX == minX && claimed.MaxX == maxX
				    && claimed.MinZ == minZ && claimed.MaxZ == maxZ) return claim;
			}
		}
		return null;
	}

	private static long ClaimKey(int minX, int minZ)
	{
		return ((long)minX << 32) | (uint)minZ;
	}

	private void LoadBackfilledClaims(ICoreServerAPI sapi)
	{
		backfilledVillageClaims.Clear();
		try
		{
			VillageClaimState state = sapi.WorldManager.SaveGame.GetData<VillageClaimState>(BackfilledClaimsKey);
			if (state?.ClaimedFootprints == null) return;
			foreach (long key in state.ClaimedFootprints) backfilledVillageClaims.Add(key);
		}
		catch (Exception ex)
		{
			// Losing the set means the backfill treats every footprint as new, so a claim an admin
			// removed could come back once. Loud, because that is the failure people notice.
			sapi.Logger.Error("[VsVillage] Could not read backfilled village claims; a removed village claim may be re-added once. " + ex.Message);
		}
	}

	private void RegisterAdminCommands(ICoreServerAPI api)
	{
		var root = api.ChatCommands.Create("vsvillage")
			.WithDescription("VSVillage admin/debug commands")
			.RequiresPrivilege(Privilege.gamemode);

		root.BeginSubCommand("trader")
			.WithDescription("Force a travelling trader to spawn at the nearest village.")
			.RequiresPlayer()
			.HandleWith(args =>
			{
				Village v = GetVillageAtPlayer(args.Caller.Player as IServerPlayer);
				if (v == null) return TextCommandResult.Error("Not within any village radius.");
				var ttm = api.ModLoader.GetModSystem<TravellingTraderManager>();
				if (ttm == null) return TextCommandResult.Error("TravellingTraderManager not loaded.");
				ttm.TryForceSpawn(v);
				return TextCommandResult.Success("Forced trader spawn for '" + v.Name + "'.");
			})
			.EndSubCommand();

		root.BeginSubCommand("gather")
			.WithDescription("Teleport all villagers in the nearest village to the mayor station immediately.")
			.RequiresPlayer()
			.HandleWith(args =>
			{
				Village v = GetVillageAtPlayer(args.Caller.Player as IServerPlayer);
				if (v == null) return TextCommandResult.Error("Not within any village radius.");
				GatherVillagers(v, teleport: true);
				return TextCommandResult.Success("Teleported " + v.Villagers.Count + " villager(s) to '" + v.Name + "' and set gather flag (3 min auto-clear).");
			})
			.EndSubCommand();

		root.BeginSubCommand("cleargather")
			.WithDescription("Release gathered villagers back to normal behaviour.")
			.RequiresPlayer()
			.HandleWith(args =>
			{
				Village v = GetVillageAtPlayer(args.Caller.Player as IServerPlayer);
				if (v == null) return TextCommandResult.Error("Not within any village radius.");
				if (v.GatherCallbackId != -1)
				{
					Api.World.UnregisterCallback(v.GatherCallbackId);
					v.GatherCallbackId = -1;
				}
				v.IsGatherActive = false;
				return TextCommandResult.Success("Gather cleared for '" + v.Name + "'.");
			})
			.EndSubCommand();

		root.BeginSubCommand("stormdrillstart")
			.WithDescription("Simulate a temporal storm for storm-shelter AI testing.")
			.HandleWith(args =>
			{
				StormDrillActive = true;
				return TextCommandResult.Success("Storm drill started - villagers will seek shelter.");
			})
			.EndSubCommand();

		root.BeginSubCommand("stormdrillend")
			.WithDescription("End the storm-shelter drill.")
			.HandleWith(args =>
			{
				StormDrillActive = false;
				return TextCommandResult.Success("Storm drill ended.");
			})
			.EndSubCommand();

		root.BeginSubCommand("validate")
			.WithDescription("Check for ghost structures in the nearest village (loaded chunks only).")
			.RequiresPlayer()
			.HandleWith(args =>
			{
				IServerPlayer player = args.Caller.Player as IServerPlayer;
				Village v = GetVillageAtPlayer(player);
				if (v == null) return TextCommandResult.Error("Not within any village radius.");
				ValidateStructures(v, player, api);
				return TextCommandResult.Success("Validation complete - see chat for results.");
			})
			.EndSubCommand();

		root.BeginSubCommand("unclaim")
			.WithDescription("Remove the village land claim covering your position. Leaves all other claims alone.")
			.RequiresPlayer()
			.RequiresPrivilege(Privilege.controlserver)
			.HandleWith(args =>
			{
				IServerPlayer player = args.Caller.Player as IServerPlayer;
				if (player?.Entity == null) return TextCommandResult.Error("No player entity.");

				BlockPos pos = player.Entity.Pos.AsBlockPos;
				LandClaim[] claims = api.World.Claims.Get(pos);
				LandClaim target = null;
				if (claims != null)
				{
					foreach (LandClaim claim in claims)
					{
						if (claim.OwnedByEntityId != VillageClaimTag) continue;
						target = claim;
						break;
					}
				}
				if (target == null) return TextCommandResult.Error("No village land claim at your position.");

				string name = target.Description ?? target.LastKnownOwnerName ?? "village";
				if (!api.World.Claims.Remove(target))
					return TextCommandResult.Error("Failed to remove the land claim on '" + name + "'.");

				return TextCommandResult.Success("Removed the land claim on '" + name + "'.");
			})
			.EndSubCommand();
	}

	// Returns the village whose radius contains the player's position, or null.
	private Village GetVillageAtPlayer(IServerPlayer player)
	{
		if (player?.Entity == null) return null;
		BlockPos playerPos = player.Entity.Pos.AsBlockPos;
		Village closest = null;
		long closestDistSq = long.MaxValue;
		foreach (Village v in Villages.Values)
		{
			if (v.Pos == null) continue;
			int dx = Math.Abs(v.Pos.X - playerPos.X);
			int dz = Math.Abs(v.Pos.Z - playerPos.Z);
			if (dx > v.Radius || dz > v.Radius) continue;
			long distSq = (long)dx * dx + (long)dz * dz;
			if (distSq < closestDistSq)
			{
				closestDistSq = distSq;
				closest = v;
			}
		}
		return closest;
	}

	// Set village gather flag, schedule 3min auto-clear. teleport=true is admin-only, never from the GUI button.
	private void GatherVillagers(Village v, bool teleport = false)
	{
		// Cancel any previous gather timer for this village.
		if (v.GatherCallbackId != -1)
		{
			Api.World.UnregisterCallback(v.GatherCallbackId);
			v.GatherCallbackId = -1;
		}

		v.IsGatherActive = true;

		if (teleport)
		{
			Vec3d centre = v.EffectiveCenter().Add(0.5, 1.0, 0.5);
			foreach (EntityBehaviorVillager beh in v.Villagers)
			{
				if (beh?.entity == null || !beh.entity.Alive) continue;
				beh.entity.TeleportTo(centre);
			}
		}

		// Auto-clear after 3 minutes.
		v.GatherCallbackId = Api.World.RegisterCallback(delegate
		{
			v.GatherCallbackId = -1;
			v.IsGatherActive = false;
		}, 180000);
	}

	// Scan loaded chunks for ghost workstations/beds and report results to .
	private void ValidateStructures(Village v, IServerPlayer player, ICoreServerAPI api)
	{
		IBlockAccessor ba = api.World.BlockAccessor;
		int ghosts = 0;

		foreach (BlockPos pos in v.Workstations.Keys.ToList())
		{
			if (pos == null || !ba.IsValidPos(pos) || ba.GetChunkAtBlockPos(pos) == null) continue;
			if (ba.GetBlockEntity<BlockEntityVillagerWorkstation>(pos) == null)
			{
				player.SendMessage(GlobalConstants.InfoLogChatGroup,
					"[VsVillage] Ghost workstation at " + pos + " (owned by entity " + v.Workstations[pos].OwnerId + ")",
					EnumChatType.Notification);
				ghosts++;
			}
		}
		foreach (BlockPos pos in v.Beds.Keys.ToList())
		{
			if (pos == null || !ba.IsValidPos(pos) || ba.GetChunkAtBlockPos(pos) == null) continue;
			if (ba.GetBlockEntity<BlockEntityVillagerBed>(pos) == null)
			{
				player.SendMessage(GlobalConstants.InfoLogChatGroup,
					"[VsVillage] Ghost bed at " + pos + " (owned by entity " + v.Beds[pos].OwnerId + ")",
					EnumChatType.Notification);
				ghosts++;
			}
		}
		if (ghosts == 0)
			player.SendMessage(GlobalConstants.InfoLogChatGroup,
				"[VsVillage] No ghost structures found in loaded chunks for '" + v.Name + "'.",
				EnumChatType.Notification);
	}

	public override void StartClientSide(ICoreClientAPI api)
	{
		base.StartClientSide(api);
		api.Network.RegisterChannel("villagemanagementnetwork")
			.RegisterMessageType<Village>()
			.SetMessageHandler(delegate(Village village)
			{
				OnVillageMessage(village, api);
			})
			.RegisterMessageType<VillageManagementMessage>()
			.RegisterMessageType<VillageAssignmentContext>()
			.SetMessageHandler(delegate(VillageAssignmentContext ctx)
			{
				OnAssignmentContext(ctx, api);
			});
		// The client runs its own tryAccess before OnBlockInteractStart, and claims are broadcast
		// to it. Without this the GUI would open locally and only fail once the packet lands.
		api.Event.OnTestBlockAccess += OnTestBlockAccess;
	}

	public Village GetVillage(string id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return null;
		}
		if (!Villages.TryGetValue(id, out var value) && Api is ICoreServerAPI coreServerAPI)
		{
			// Hoisted out of the try so the catch can include the byte length in
			// its diagnostic - knowing if SaveGame.GetData even returned bytes is
			// half the battle when the deserializer throws.
			byte[] data = null;
			try
			{
				data = coreServerAPI.WorldManager.SaveGame.GetData(id);
				value = ((data == null || data.Length < 10) ? null : SerializerUtil.Deserialize<Village>(data));
				value?.Init(coreServerAPI);
				if (value != null)
				{
					// Villages saved before names were resolved still hold the raw addon lang key.
					value.Name = Lang.GetUnformatted(value.Name ?? "");
					Villages.TryAdd(id, value);
				}
			}
			catch (Exception ex)
			{
				// Do NOT TryAdd a fallback village here. The previous code
				// inserted a placeholder "Lauras little Village" and then the
				// next world save overwrote the original bytes on disk,
				// permanently destroying the real village state. Log loudly
				// instead and leave the disk bytes untouched so manual recovery
				// is possible. Caller gets null and behaves as if the village
				// is absent until the load succeeds (e.g. after a server
				// restart or schema migration).
				int dataLen = (data == null) ? -1 : data.Length;
				Api.Logger.Error("[VsVillage] Village id=" + id + " failed to deserialize (" + dataLen + " bytes on disk). Disk bytes preserved for manual recovery; this village will appear absent until load succeeds. Exception:");
				Api.Logger.Error(ex);
				value = null;
			}
		}
		return value;
	}

	public Village GetVillage(BlockPos pos)
	{
		// Axis-aligned box to match IsWithinVillageRadius and other village-registration checks.
		foreach (Village value in Villages.Values)
		{
			if (value.Pos == null) continue;
			int dx = value.Pos.X - pos.X;
			int dz = value.Pos.Z - pos.Z;
			if (Math.Abs(dx) <= value.Radius && Math.Abs(dz) <= value.Radius)
			{
				return value;
			}
		}
		return null;
	}

	private void OnSave(ICoreServerAPI sapi)
	{
		sapi.WorldManager.SaveGame.StoreData(BackfilledClaimsKey,
			new VillageClaimState { ClaimedFootprints = backfilledVillageClaims.ToList() });

		foreach (Village value in Villages.Values)
		{
			try
			{
				sapi.WorldManager.SaveGame.StoreData(value.Id, SerializerUtil.Serialize(value));
			}
			catch (Exception ex)
			{
				sapi.Logger.Error("[VsVillage] Failed to save village '" + value.Id + "': " + ex.Message + ". Continuing with remaining villages.");
			}
		}
	}

	public void RemoveVillage(string id)
	{
		if (!Villages.TryRemove(id, out Village village)) return;
		if (Api is ICoreServerAPI sapi)
		{
			UnregisterVillageClaim(sapi, village);
			sapi.WorldManager.SaveGame.StoreData(id, null);
		}
		village.Workstations.Values.Foreach(delegate(VillagerWorkstation workstation)
		{
			Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(workstation.Pos)?.RemoveVillage();
		});
		village.Gatherplaces.Foreach(delegate(BlockPos gatherplace)
		{
			Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBrazier>(gatherplace)?.RemoveVillage();
		});
		village.Beds.Values.Foreach(delegate(VillagerBed bed)
		{
			Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(bed.Pos)?.RemoveVillage();
		});
		village.Villagers.ForEach(delegate(EntityBehaviorVillager villager)
		{
			villager?.RemoveVillage();
		});
	}

	private void OnVillageMessage(Village village, ICoreClientAPI capi)
	{
		village.Init(capi);
		new ManagementGui(capi, village.Pos, village).TryOpen();
	}

	private void OnAssignmentContext(VillageAssignmentContext ctx, ICoreClientAPI capi)
	{
		new AssignVillagerGui(capi, ctx).TryOpen();
	}

	// Radius arrives straight off the wire from a GUI text field, so bound it. 500 is the agreed
	// ceiling; past that the village-wide scans and fixture recovery get genuinely painful.
	public const int MaxVillageRadius = 500;

	private static int ClampRadius(int requested)
	{
		if (requested <= 0) return 20;
		return requested > MaxVillageRadius ? MaxVillageRadius : requested;
	}

	private void OnManagementMessage(IServerPlayer fromPlayer, VillageManagementMessage message, ICoreServerAPI api)
	{
		switch (message.Operation)
		{
		case EnumVillageManagementOperation.create:
		{
			// The client opens the GUI off its own access test, which can't see this hook, so the
			// packet is the only authoritative gate on founding a village inside a claim.
			if (fromPlayer != null && message.Pos != null
			    && !api.World.Claims.TryAccess(fromPlayer, message.Pos, EnumBlockAccessFlags.Use)) break;

			Village village5 = new Village
			{
				Radius = ClampRadius(message.Radius),
				Pos = message.Pos,
				Name = (string.IsNullOrEmpty(message.Name) ? "Lauras little Village" : message.Name)
			};
			village5.Init(api);
			BlockEntityVillagerWorkstation blockEntity = api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(message.Pos);
			if (blockEntity == null)
			{
				Api.Logger.Error("[VsVillage] create: no workstation block entity at " + message.Pos + " - village creation aborted.");
				break;
			}
			blockEntity.VillageId = village5.Id;
			blockEntity.VillageName = village5.Name;
			blockEntity.MarkDirty();
			village5.Workstations.Add(message.Pos, new VillagerWorkstation
			{
				OwnerId = -1L,
				Pos = message.Pos,
				Profession = blockEntity.Profession
			});
			Villages.TryAdd(village5.Id, village5);
			break;
		}
		case EnumVillageManagementOperation.destroy:
			RemoveVillage(message.Id);
			break;
		case EnumVillageManagementOperation.removeVillager:
		{
			Village village4 = GetVillage(message.Id);
			Entity dismissedEntity = Api.World.GetEntityById(message.VillagerToRemove);
			dismissedEntity?.GetBehavior<EntityBehaviorVillager>()?.RemoveVillage();
			village4?.RemoveVillager(message.VillagerToRemove);
			if (dismissedEntity != null && dismissedEntity.Alive)
			{
				(Api as ICoreServerAPI)?.World.DespawnEntity(dismissedEntity, new EntityDespawnData
				{
					Reason = EnumDespawnReason.Removed
				});
			}
			break;
		}
		case EnumVillageManagementOperation.removeStructure:
		{
			// BUG FIX: GUI sends StructureToRemove, not Pos (Pos is the village centre).
			BlockPos structPos = message.StructureToRemove;
			if (structPos == null)
			{
				Api.Logger.Error("[VsVillage] removeStructure message has null StructureToRemove - ignoring.");
				break;
			}
			Village village3 = GetVillage(message.Id);
			if (village3 == null)
			{
				Api.Logger.Error("[VsVillage] removeStructure: village '" + message.Id + "' not found - ignoring.");
				break;
			}
			// Notify the owning villager so they seek a new workstation immediately.
			if (village3.Workstations.TryGetValue(structPos, out VillagerWorkstation removedWs) && removedWs.OwnerId != -1)
			{
				Entity wsOwner = Api.World.GetEntityById(removedWs.OwnerId);
				EntityBehaviorVillager wsOwnerBeh = wsOwner?.GetBehavior<EntityBehaviorVillager>();
				if (wsOwnerBeh != null) wsOwnerBeh.Workstation = null;
			}
			if (village3.Workstations.Remove(structPos))
			{
				api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(structPos)?.RemoveVillage();
			}
			if (village3.Gatherplaces.Remove(structPos))
			{
				api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBrazier>(structPos)?.RemoveVillage();
			}
			// Notify the bed owner so they find a new bed.
			if (village3.Beds.TryGetValue(structPos, out VillagerBed removedBed) && removedBed.OwnerId != -1)
			{
				Entity bedOwner = Api.World.GetEntityById(removedBed.OwnerId);
				EntityBehaviorVillager bedOwnerBeh = bedOwner?.GetBehavior<EntityBehaviorVillager>();
				if (bedOwnerBeh != null) bedOwnerBeh.Bed = null;
			}
			if (village3.Beds.Remove(structPos))
			{
				api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(structPos)?.RemoveVillage();
			}
			break;
		}
		case EnumVillageManagementOperation.markStructureInvalid:
		{
			// Force-remove a ghost entry - block entity may not exist, so skip the BE call.
			BlockPos ghostPos = message.StructureToRemove;
			if (ghostPos == null) { Api.Logger.Error("[VsVillage] markStructureInvalid: null pos - ignoring."); break; }
			Village ghostVillage = GetVillage(message.Id);
			if (ghostVillage == null) { Api.Logger.Error("[VsVillage] markStructureInvalid: village '" + message.Id + "' not found."); break; }

			// Free owning villager references before removing.
			if (ghostVillage.Workstations.TryGetValue(ghostPos, out VillagerWorkstation ghostWs) && ghostWs.OwnerId != -1)
			{
				EntityBehaviorVillager ghostWsOwner = Api.World.GetEntityById(ghostWs.OwnerId)?.GetBehavior<EntityBehaviorVillager>();
				if (ghostWsOwner != null) ghostWsOwner.Workstation = null;
			}
			if (ghostVillage.Beds.TryGetValue(ghostPos, out VillagerBed ghostBed) && ghostBed.OwnerId != -1)
			{
				EntityBehaviorVillager ghostBedOwner = Api.World.GetEntityById(ghostBed.OwnerId)?.GetBehavior<EntityBehaviorVillager>();
				if (ghostBedOwner != null) ghostBedOwner.Bed = null;
			}

			ghostVillage.Workstations.Remove(ghostPos);
			ghostVillage.Gatherplaces.Remove(ghostPos);
			ghostVillage.Beds.Remove(ghostPos);
			Api.Logger.Notification("[VsVillage] Forcibly removed ghost structure at " + ghostPos + " from village '" + ghostVillage.Name + "'.");
			break;
		}
		case EnumVillageManagementOperation.gatherVillagers:
		{
			Village gv = GetVillage(message.Id);
			if (gv == null) { Api.Logger.Error("[VsVillage] gatherVillagers: village '" + message.Id + "' not found."); break; }
			GatherVillagers(gv);
			break;
		}
		case EnumVillageManagementOperation.clearGather:
		{
			Village cgv = GetVillage(message.Id);
			if (cgv != null)
			{
				if (cgv.GatherCallbackId != -1)
				{
					Api.World.UnregisterCallback(cgv.GatherCallbackId);
					cgv.GatherCallbackId = -1;
				}
				cgv.IsGatherActive = false;
			}
			break;
		}
		case EnumVillageManagementOperation.validateStructures:
		{
			Village vv = GetVillage(message.Id);
			if (vv == null) { Api.Logger.Error("[VsVillage] validateStructures: village '" + message.Id + "' not found."); break; }
			ValidateStructures(vv, fromPlayer, api);
			break;
		}
		case EnumVillageManagementOperation.changeStats:
		{
			Village village2 = GetVillage(message.Id);
			if (village2 == null) { Api.Logger.Error("[VsVillage] changeStats: village '" + message.Id + "' not found."); break; }
			village2.Radius = ClampRadius(message.Radius);
			village2.Name = message.Name;
			break;
		}
		case EnumVillageManagementOperation.dismissMechhelper:
		{
			Village dmv = GetVillage(message.Id);
			if (dmv?.Pos == null)
			{
				Api.Logger.Error("[VsVillage] dismissMechhelper: village '" + message.Id + "' not found or has no Pos.");
				break;
			}
			// Look in a 60-block radius around the mayor workstation - same scope the
			// Villager Horn uses when checking for an existing keeper, so the GUI and
			// the horn agree on which mechhelper "belongs" to this village.
			Vec3d centre = dmv.EffectiveCenter().Add(0.5, 0.5, 0.5);
			Entity[] keepers = api.World.GetEntitiesAround(centre, 60f, 20f,
				e => e.Code?.Domain == "vsvillage" && e.Code?.Path == "village-mechhelper" && e.Alive);

			int dismissed = 0;
			foreach (Entity k in keepers)
			{
				api.World.DespawnEntity(k, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
				dismissed++;
			}

			// Brief poof effect so the player gets visual feedback at the village centre.
			SimpleParticleProperties poof = new SimpleParticleProperties(
				30f, 60f, ColorUtil.ToRgba(75, 169, 169, 169),
				new Vec3d(), new Vec3d(2.0, 1.0, 2.0),
				new Vec3f(-0.25f, 0f, -0.25f), new Vec3f(0.25f, 0f, 0.25f),
				3f, -0.075f, 0.5f, 3f, EnumParticleModel.Quad);
			poof.MinPos = centre;
			api.World.SpawnParticles(poof);

			fromPlayer.SendMessage(GlobalConstants.InfoLogChatGroup,
				dismissed > 0
					? "[VsVillage] Dismissed " + dismissed + " Settlement Keeper(s) from '" + dmv.Name + "'."
					: "[VsVillage] No Settlement Keeper found near '" + dmv.Name + "'.",
				EnumChatType.Notification);
			break;
		}
		case EnumVillageManagementOperation.recoverOrphanedVillagers:
		{
			Village rv = GetVillage(message.Id);
			if (rv == null) { Api.Logger.Error("[VsVillage] recoverOrphanedVillagers: village '" + message.Id + "' not found."); break; }

			Vec3d rvCenter = rv.EffectiveCenter().Add(0.5, 0.5, 0.5);
			float rvRadius = (float)rv.Radius;
			Entity[] candidates = api.World.GetEntitiesAround(rvCenter, rvRadius, rvRadius, e =>
			{
				EntityBehaviorVillager bv = e.GetBehavior<EntityBehaviorVillager>();
				if (bv == null) return false;
				// Only touch vsvillage entities - don't grab other mods' villagers.
				if (e.Code?.Domain != "vsvillage") return false;
				// Skip villagers already registered to this village.
				if (bv.VillageId == rv.Id) return false;
				string vid = bv.VillageId;
				// Catch both: stale VillageId (deleted village) and empty VillageId (hire bug / unassigned).
				return string.IsNullOrEmpty(vid) || GetVillage(vid) == null;
			});

			int recovered = 0;
			foreach (Entity e in candidates)
			{
				EntityBehaviorVillager bv = e.GetBehavior<EntityBehaviorVillager>();
				if (bv == null) continue;
				bv.Village = rv;
				rv.VillagerSaveData[e.EntityId] = new VillagerData
				{
					Id = e.EntityId,
					Profession = bv.Profession,
					Name = e.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? ""
				};
				recovered++;
				Api.Logger.Notification("[VsVillage] Recovered orphaned villager " + e.EntityId + " into village '" + rv.Name + "'.");
			}

			fromPlayer.SendMessage(GlobalConstants.InfoLogChatGroup,
				"[VsVillage] Recovered " + recovered + " orphaned villager(s) into '" + rv.Name + "'.",
				EnumChatType.Notification);
			break;
		}
		case EnumVillageManagementOperation.recoverFixtures:
		{
			Village rfv = GetVillage(message.Id);
			if (rfv == null) { Api.Logger.Error("[VsVillage] recoverFixtures: village '" + message.Id + "' not found."); break; }

			// Remove ghost dict entries before the walk so re-adds are clean.
			rfv.ScrubGhostStructures();

			int recovered = 0;
			int r = rfv.Radius;
			int cy = rfv.EffectiveCenterY();
			int dim = rfv.Pos.dimension;
			IBlockAccessor ba = api.World.BlockAccessor;
			BlockPos minPos = new BlockPos(rfv.Pos.X - r, cy - r, rfv.Pos.Z - r, dim);
			BlockPos maxPos = new BlockPos(rfv.Pos.X + r, cy + r, rfv.Pos.Z + r, dim);
			BlockPos tmp = new BlockPos(0, 0, 0, dim);

			ba.WalkBlocks(minPos, maxPos, (block, x, y, z) =>
			{
				tmp.Set(x, y, z);
				BlockEntity be = ba.GetBlockEntity(tmp);
				if (!(be is BlockEntityVillagerPOI poi)) return;

				bool inData = (poi is BlockEntityVillagerWorkstation && rfv.Workstations.ContainsKey(tmp))
					|| (poi is BlockEntityVillagerBed && rfv.Beds.ContainsKey(tmp))
					|| (poi is BlockEntityVillagerBrazier && rfv.Gatherplaces.Contains(tmp))
					|| (poi is BlockEntityVillagerWaypoint && rfv.Waypoints.Contains(tmp));
				if (inData) return;

				// Belongs to a different valid village - leave it alone.
				if (!string.IsNullOrEmpty(poi.VillageId) && poi.VillageId != rfv.Id && GetVillage(poi.VillageId) != null) return;

				poi.VillageId = rfv.Id;
				poi.VillageName = rfv.Name;
				poi.AddToVillage(rfv);
				poi.MarkDirty();
				recovered++;
				Api.Logger.Notification("[VsVillage] recoverFixtures: re-registered " + be.GetType().Name + " at " + tmp + " into '" + rfv.Name + "'.");
			});

			// Prune completely empty orphan villages left behind when a mayor block
			// was broken without using the GUI destroy operation.
			var pruned = new List<string>();
			foreach (var kvp in Villages)
			{
				Village v = kvp.Value;
				if (v == rfv) continue;
				if (v.Workstations.Count == 0 && v.Beds.Count == 0 && v.Gatherplaces.Count == 0)
				{
					pruned.Add(kvp.Key);
					Api.Logger.Notification("[VsVillage] recoverFixtures: pruning structureless orphan village '" + kvp.Key + "'.");
				}
			}
			foreach (string key in pruned) RemoveVillage(key);

			fromPlayer.SendMessage(GlobalConstants.InfoLogChatGroup,
				"[VsVillage] Recovered " + recovered + " fixture(s) into '" + rfv.Name + "'"
				+ (pruned.Count > 0 ? $", pruned {pruned.Count} orphan village(s)." : "."),
				EnumChatType.Notification);
			break;
		}
		case EnumVillageManagementOperation.hireVillager:
		{
			Village village = GetVillage(message.Id);
			TryHireVillager(message.VillagerProfession, message.VillagerType, village, fromPlayer);
			break;
		}
		case EnumVillageManagementOperation.assignWorkstation:
		{
			Village v = GetVillage(message.Id);
			if (v == null) { Api.Logger.Error("[VsVillage] assignWorkstation: village '" + message.Id + "' not found."); break; }
			BlockPos wsPos = message.StructureToAssign;
			if (wsPos == null || !v.Workstations.TryGetValue(wsPos, out VillagerWorkstation ws))
			{
				Api.Logger.Error("[VsVillage] assignWorkstation: no workstation at " + wsPos + " in village '" + v.Name + "'.");
				break;
			}
			long newOwnerId = message.AssigneeEntityId;

			// Validate the requester owns a villager in this village.
			if (newOwnerId != -1 && !v.VillagerSaveData.ContainsKey(newOwnerId))
			{
				fromPlayer.SendIngameError("assignment-invalid-villager", null);
				break;
			}

			// Run base-level requirement check (no scaling).
			if (newOwnerId != -1)
			{
				string error = VillagerHireRequirementChecker.CheckRequirementsForAssignment(ws.Profession, wsPos, v, api);
				if (error != null) { fromPlayer.SendIngameError("assignment-requirements-not-met", error); break; }
			}

			// Free old owner's Workstation WatchedAttribute (if loaded).
			if (ws.OwnerId != -1 && ws.OwnerId != newOwnerId)
			{
				EntityBehaviorVillager oldBeh = Api.World.GetEntityById(ws.OwnerId)?.GetBehavior<EntityBehaviorVillager>();
				if (oldBeh != null) oldBeh.Workstation = null;
			}

			// Remove new owner from their previous workstation (if they had one).
			if (newOwnerId != -1)
			{
				EntityBehaviorVillager newBeh = Api.World.GetEntityById(newOwnerId)?.GetBehavior<EntityBehaviorVillager>();
				if (newBeh?.Workstation != null && v.Workstations.TryGetValue(newBeh.Workstation, out VillagerWorkstation prevWs))
					prevWs.OwnerId = -1L;
				if (newBeh != null) newBeh.Workstation = wsPos;
			}

			ws.OwnerId = newOwnerId;
			// Update the block entity's displayed owner name.
			string newOwnerName = newOwnerId != -1
				? (api.World.GetEntityById(newOwnerId)?.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "")
				: null;
			BlockEntityVillagerWorkstation wsEntity = api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(wsPos);
			if (wsEntity != null) { wsEntity.OwnerName = newOwnerName; wsEntity.MarkDirty(); }

			// The stall keeps its specialty, so an incoming trader takes it over rather
			// than reverting the shop to the default list.
			if (ws.Profession == EnumVillagerProfession.trader && newOwnerId != -1)
			{
				TraderShopType.ApplyForWorkstation(api.World.GetEntityById(newOwnerId), ws.ShopType);
			}

			Api.Logger.Debug("[VsVillage] Workstation at " + wsPos + " in '" + v.Name + "' assigned to entity " + newOwnerId + ".");
			break;
		}
		case EnumVillageManagementOperation.assignBed:
		{
			Village v = GetVillage(message.Id);
			if (v == null) { Api.Logger.Error("[VsVillage] assignBed: village '" + message.Id + "' not found."); break; }
			BlockPos bedPos = message.StructureToAssign;
			if (bedPos == null || !v.Beds.TryGetValue(bedPos, out VillagerBed bed))
			{
				Api.Logger.Error("[VsVillage] assignBed: no bed at " + bedPos + " in village '" + v.Name + "'.");
				break;
			}
			long newOwnerId = message.AssigneeEntityId;

			// Validate the requester owns a villager in this village.
			if (newOwnerId != -1 && !v.VillagerSaveData.ContainsKey(newOwnerId))
			{
				fromPlayer.SendIngameError("assignment-invalid-villager", null);
				break;
			}

			// Validate bed is indoors.
			if (newOwnerId != -1)
			{
				string error = VillagerHireRequirementChecker.CheckBedIndoors(bedPos, api);
				if (error != null) { fromPlayer.SendIngameError("assignment-requirements-not-met", error); break; }
			}

			// Free old owner's Bed WatchedAttribute (if loaded).
			if (bed.OwnerId != -1 && bed.OwnerId != newOwnerId)
			{
				EntityBehaviorVillager oldBeh = Api.World.GetEntityById(bed.OwnerId)?.GetBehavior<EntityBehaviorVillager>();
				if (oldBeh != null) oldBeh.Bed = null;
			}

			// Remove new owner from their previous bed (if they had one).
			if (newOwnerId != -1)
			{
				EntityBehaviorVillager newBeh = Api.World.GetEntityById(newOwnerId)?.GetBehavior<EntityBehaviorVillager>();
				if (newBeh?.Bed != null && v.Beds.TryGetValue(newBeh.Bed, out VillagerBed prevBed))
					prevBed.OwnerId = -1L;
				if (newBeh != null) newBeh.Bed = bedPos;
			}

			bed.OwnerId = newOwnerId;
			// Update the block entity's displayed owner name.
			string newOwnerName2 = newOwnerId != -1
				? (api.World.GetEntityById(newOwnerId)?.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "")
				: null;
			BlockEntityVillagerBed bedEntity = api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(bedPos);
			if (bedEntity != null) { bedEntity.OwnerName = newOwnerName2; bedEntity.MarkDirty(); }

			Api.Logger.Debug("[VsVillage] Bed at " + bedPos + " in '" + v.Name + "' assigned to entity " + newOwnerId + ".");
			break;
		}
		case EnumVillageManagementOperation.assignGuardPost:
		{
			Village v = GetVillage(message.Id);
			if (v == null) { Api.Logger.Error("[VsVillage] assignGuardPost: village '" + message.Id + "' not found."); break; }
			BlockPos postPos = message.StructureToAssign;
			if (postPos == null || !v.GuardPosts.TryGetValue(postPos, out VillagerGuardPost post))
			{
				Api.Logger.Error("[VsVillage] assignGuardPost: no guard post at " + postPos + " in village '" + v.Name + "'.");
				break;
			}
			EnumGuardShift shift = message.GuardShift;
			if (shift == EnumGuardShift.none)
			{
				Api.Logger.Error("[VsVillage] assignGuardPost: no watch specified for post at " + postPos + ".");
				break;
			}
			long newOwnerId = message.AssigneeEntityId;

			// Validate the requester owns a villager in this village.
			if (newOwnerId != -1 && !v.VillagerSaveData.ContainsKey(newOwnerId))
			{
				fromPlayer.SendIngameError("assignment-invalid-villager", null);
				break;
			}

			if (newOwnerId != -1 && !GuardDuty.CanStandWatch(v.VillagerSaveData[newOwnerId].Profession))
			{
				fromPlayer.SendIngameError("assignment-requirements-not-met", Lang.Get("vsvillage:guardpost-requires-soldier"));
				break;
			}

			// Free the outgoing guard of this watch.
			long oldOwnerId = post.OwnerFor(shift);
			if (oldOwnerId != -1 && oldOwnerId != newOwnerId) ReleaseGuardAttributes(oldOwnerId);

			if (newOwnerId != -1)
			{
				// One guard holds exactly one watch at one post. Clearing every other slot first is
				// what stops the same guard being booked onto both day and night.
				foreach (VillagerGuardPost other in v.GuardPosts.Values)
				{
					if (other.DayOwnerId == newOwnerId) other.DayOwnerId = -1L;
					if (other.NightOwnerId == newOwnerId) other.NightOwnerId = -1L;
				}

				EntityBehaviorVillager newBeh = Api.World.GetEntityById(newOwnerId)?.GetBehavior<EntityBehaviorVillager>();
				if (newBeh != null)
				{
					newBeh.GuardPost = postPos;
					newBeh.GuardShift = shift;
				}
			}

			post.SetOwnerFor(shift, newOwnerId);

			BlockEntityVillagerGuardPost postEntity = api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerGuardPost>(postPos);
			postEntity?.MarkDirty();

			Api.Logger.Debug("[VsVillage] Guard post at " + postPos + " in '" + v.Name + "' " + shift + " watch assigned to entity " + newOwnerId + ".");
			break;
		}
		case EnumVillageManagementOperation.changeShopType:
		{
			Village v = GetVillage(message.Id);
			if (v == null) { Api.Logger.Error("[VsVillage] changeShopType: village '" + message.Id + "' not found."); break; }
			BlockPos shopPos = message.StructureToAssign;
			if (shopPos == null || !v.Workstations.TryGetValue(shopPos, out VillagerWorkstation shopWs))
			{
				Api.Logger.Error("[VsVillage] changeShopType: no workstation at " + shopPos + " in village '" + v.Name + "'.");
				break;
			}
			if (fromPlayer?.InventoryManager == null)
			{
				Api.Logger.Error("[VsVillage] changeShopType: sender has no inventory manager.");
				break;
			}
			if (shopWs.Profession != EnumVillagerProfession.trader)
			{
				fromPlayer.SendIngameError("shoptype-wrong-workstation", null);
				break;
			}
			string requestedType = message.ShopType;
			if (!TraderShopType.IsKnown(requestedType))
			{
				fromPlayer.SendIngameError("shoptype-invalid", null);
				break;
			}
			// Re-picking the current type is free rather than a wasted payment.
			if (TraderShopType.Sanitize(shopWs.ShopType) == requestedType) break;
			// Never charge for a list that will fail to load when it comes time to apply it.
			if (!TraderShopType.ListExists(api.Assets, requestedType))
			{
				Api.Logger.Error("[VsVillage] changeShopType: no tradelist asset for shop type '" + requestedType + "'.");
				fromPlayer.SendIngameError("shoptype-invalid", null);
				break;
			}

			Entity shopOwner = (shopWs.OwnerId != -1L) ? Api.World.GetEntityById(shopWs.OwnerId) : null;
			// Swapping the stock out from under an open trade dialog desyncs it.
			if (shopOwner != null && shopOwner.WatchedAttributes.HasAttribute("tradingPlayerUID"))
			{
				fromPlayer.SendIngameError("shoptype-busy", null);
				break;
			}

			int shopCost = TraderShopType.CostFor(shopWs.ShopTypeChanges);
			if (CountRustyGears(fromPlayer) < shopCost)
			{
				fromPlayer.SendIngameError("shoptype-not-enough-gears", null, shopCost);
				break;
			}
			TakeRustyGears(fromPlayer, shopCost);

			shopWs.ShopType = requestedType;
			shopWs.ShopTypeChanges++;

			BlockEntityVillagerWorkstation shopEntity = api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(shopPos);
			if (shopEntity != null) { shopEntity.ShopType = requestedType; shopEntity.MarkDirty(); }

			// An unloaded trader picks this up in EntityBehaviorVillager's post-load init instead.
			if (shopOwner != null) TraderShopType.ApplyForWorkstation(shopOwner, requestedType);

			fromPlayer.Entity?.World.PlaySoundFor(new AssetLocation("sounds/effect/cashregister"), fromPlayer, randomizePitch: false, 32f, 0.25f);
			Api.Logger.Debug("[VsVillage] Trader workstation at " + shopPos + " in '" + v.Name + "' set to shop type '" + requestedType + "' for " + shopCost + " gears.");
			break;
		}
		}
	}

	// Unloaded guards keep the stale attributes; GuardDuty.ValidateAssignment clears those on next tick.
	private void ReleaseGuardAttributes(long ownerId)
	{
		EntityBehaviorVillager beh = Api.World.GetEntityById(ownerId)?.GetBehavior<EntityBehaviorVillager>();
		if (beh == null) return;
		beh.GuardPost = null;
		beh.GuardShift = EnumGuardShift.none;
	}

	private static int CountRustyGears(IServerPlayer player)
	{
		int total = 0;
		foreach (IInventory inv in player.InventoryManager.Inventories.Values)
		{
			if (inv.ClassName == "creative") continue;
			foreach (ItemSlot slot in inv)
			{
				if (slot?.Itemstack?.Collectible?.Code?.Path == "gear-rusty") total += slot.Itemstack.StackSize;
			}
		}
		return total;
	}

	private static void TakeRustyGears(IServerPlayer player, int amount)
	{
		int taken = 0;
		foreach (IInventory inv in player.InventoryManager.Inventories.Values)
		{
			if (inv.ClassName == "creative") continue;
			foreach (ItemSlot slot in inv)
			{
				if (taken >= amount) break;
				if (slot?.Itemstack?.Collectible?.Code?.Path != "gear-rusty") continue;
				ItemStack removed = slot.TakeOut(Math.Min(slot.Itemstack.StackSize, amount - taken));
				slot.MarkDirty();
				taken += removed.StackSize;
			}
			if (taken >= amount) break;
		}
	}

	private bool TryHireVillager(EnumVillagerProfession profession, string type, Village village, IServerPlayer fromPlayer)
	{
		bool result;
		if (village == null)
		{
			Api.Logger.Error("TryHireVillager called with null village");
			result = false;
		}
		else if (village.Beds == null || village.Workstations == null || village.VillagerSaveData == null)
		{
			ILogger logger = Api.Logger;
			logger.Error($"Village {village.Id} has null collections - Beds:{village.Beds == null}, Workstations:{village.Workstations == null}, VillagerSaveData:{village.VillagerSaveData == null}");
			fromPlayer?.SendIngameError("village-not-initialized", null);
			result = false;
		}
		else if (fromPlayer?.InventoryManager == null)
		{
			Api.Logger.Error("TryHireVillager called with null player inventory");
			result = false;
		}
		else if (village.Beds.Values.Where((VillagerBed bed) => bed.OwnerId == -1).Count() == 0)
		{
			fromPlayer.SendIngameError("not-enough-beds", null);
			result = false;
		}
		else if (village.Workstations.Values.Where((VillagerWorkstation workstation) => workstation.Profession == profession && workstation.OwnerId == -1).Count() == 0)
		{
			fromPlayer.SendIngameError("not-enough-workstations", null);
			result = false;
		}
		else
		{
			VillagerWorkstation targetWorkstation = village.Workstations.Values.First((VillagerWorkstation ws) => ws.Profession == profession && ws.OwnerId == -1);
			string requirementError = VillagerHireRequirementChecker.CheckRequirements(profession, targetWorkstation.Pos, village, Api);
			if (requirementError != null)
			{
				fromPlayer.SendIngameError("hire-requirements-not-met", requirementError);
				return false;
			}
			if (profession != EnumVillagerProfession.farmer && profession != EnumVillagerProfession.shepherd)
			{
				List<EntityBehaviorVillager> list = village.Villagers.Where((EntityBehaviorVillager v) => v != null).ToList();
				int farmers = list.Count((EntityBehaviorVillager v) => v.Profession == EnumVillagerProfession.farmer);
				int shepherds = list.Count((EntityBehaviorVillager v) => v.Profession == EnumVillagerProfession.shepherd);
				int bakers = list.Count((EntityBehaviorVillager v) => v.Profession == EnumVillagerProfession.baker);
				// Tuning: farmer feeds 3 (themselves + 2 others), shepherd feeds 2,
				// baker contributes 1. Previously farmer + shepherd grouped at 2 each,
				// which was too grindy for the early village ramp.
				if (3 * farmers + 2 * shepherds + bakers - list.Count <= 0)
				{
					fromPlayer.SendIngameError("not-enough-food", null);
					return false;
				}
			}
			int num2 = 0;
			foreach (IInventory value in fromPlayer.InventoryManager.Inventories.Values)
			{
				if (value.ClassName == "creative")
				{
					continue;
				}
				foreach (ItemSlot item in value)
				{
					if (item?.Itemstack?.Collectible?.Code?.Path == "gear-rusty")
					{
						num2 += item.Itemstack.StackSize;
					}
				}
			}
			if (num2 < villagerHiringCost)
			{
				fromPlayer.SendIngameError("not-enough-gears", null);
				result = false;
			}
			else if (!(fromPlayer.Entity.Api.World is IServerWorldAccessor serverWorldAccessor))
			{
				Api.Logger.Error("Could not get server world accessor");
				result = false;
			}
			else
			{
				string text2 = string.Format("vsvillage:villager-{0}-{1}", (serverWorldAccessor.Rand.Next(0, 2) == 0) ? "male" : "female", type);
				EntityProperties entityType = serverWorldAccessor.GetEntityType(new AssetLocation(text2));
				if (entityType == null)
				{
					fromPlayer.SendIngameError("no-valid-villager", null, text2);
					result = false;
				}
				else
				{
					Entity entity = serverWorldAccessor.ClassRegistry.CreateEntity(entityType);
					if (entity == null)
					{
						Api.Logger.Error("Failed to create entity of type " + text2);
						result = false;
					}
					else
					{
						// Pick the free bed CLOSEST to the workstation - villagers should sleep
						// near where they work. Avoids the old "first inserted bed wins" bug
						// where a new farmer would take the mayor's edge-of-village bed.
						BlockPos wsPosForDist = targetWorkstation.Pos;
						VillagerBed villagerBed = village.Beds.Values
							.Where(bed => bed.OwnerId == -1)
							.OrderBy(bed =>
							{
								int dx = bed.Pos.X - wsPosForDist.X;
								int dy = bed.Pos.Y - wsPosForDist.Y;
								int dz = bed.Pos.Z - wsPosForDist.Z;
								return dx * dx + dy * dy + dz * dz;
							})
							.First();
						entity.Pos.X = villagerBed.Pos.X + 0.5;
						entity.Pos.Y = villagerBed.Pos.Y + 1;
						entity.Pos.Z = villagerBed.Pos.Z + 0.5;
						serverWorldAccessor.SpawnEntity(entity);
						villagerBed.OwnerId = entity.EntityId;
						targetWorkstation.OwnerId = entity.EntityId;
						EntityBehaviorVillager behavior = entity.GetBehavior<EntityBehaviorVillager>();
						if (behavior != null)
						{
							behavior.Bed = villagerBed.Pos;
							behavior.Workstation = targetWorkstation.Pos;
							behavior.Village = village;
							village.VillagerSaveData[entity.EntityId] = new VillagerData
							{
								Id = entity.EntityId,
								Profession = behavior.Profession,
								Name = entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? ""
							};
						}
						else
						{
							Api.Logger.Warning("Spawned villager entity " + text2 + " is missing EntityBehaviorVillager behavior");
						}
						// Update block entity owner names so GUI shows them immediately.
						string hiredName = entity.GetBehavior<EntityBehaviorNameTag>()?.DisplayName ?? "";
						BlockEntityVillagerBed bedEntity = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerBed>(villagerBed.Pos);
						if (bedEntity != null) { bedEntity.OwnerName = hiredName; bedEntity.MarkDirty(); }
						BlockEntityVillagerWorkstation wsEntity2 = Api.World.BlockAccessor.GetBlockEntity<BlockEntityVillagerWorkstation>(targetWorkstation.Pos);
						if (wsEntity2 != null) { wsEntity2.OwnerName = hiredName; wsEntity2.MarkDirty(); }
						fromPlayer.Entity.World.PlaySoundFor(new AssetLocation("sounds/effect/cashregister"), fromPlayer, randomizePitch: false, 32f, 0.25f);
						num2 = 0;
						foreach (IInventory value2 in fromPlayer.InventoryManager.Inventories.Values)
						{
							if (value2.ClassName == "creative")
							{
								continue;
							}
							foreach (ItemSlot item2 in value2)
							{
								if (item2?.Itemstack?.Collectible?.Code?.Path == "gear-rusty")
								{
									ItemStack itemStack = item2.TakeOut(Math.Min(item2.Itemstack.StackSize, villagerHiringCost - num2));
									item2.MarkDirty();
									num2 += itemStack.StackSize;
								}
								if (num2 >= villagerHiringCost)
								{
									break;
								}
							}
							if (num2 < villagerHiringCost)
							{
								continue;
							}
							break;
						}
						result = true;
					}
				}
			}
		}
		return result;
	}
}

[ProtoContract]
internal class VillageClaimState
{
	[ProtoMember(1)] public List<long> ClaimedFootprints { get; set; } = new List<long>();
}
