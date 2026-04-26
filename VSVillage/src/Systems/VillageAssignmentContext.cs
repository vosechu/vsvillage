using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

/// <summary>
/// Sent server→client when a player right-clicks a workstation or bed that belongs
/// to a village.  The client uses this to open <see cref="AssignVillagerGui"/>.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class VillageAssignmentContext
{
	/// <summary>Full village data — contains VillagerSaveData, Workstations, Beds, etc.</summary>
	public Village Village;

	/// <summary>World position of the clicked workstation or bed.</summary>
	public BlockPos StructurePos;

	/// <summary>True when the clicked block is a bed; false when it's a workstation.</summary>
	public bool IsBed;

	/// <summary>
	/// Total resource count relevant to this workstation's profession:
	/// farmland blocks for farmers, enclosed animals for shepherds.
	/// -1 means no stats available (other professions, or beds).
	/// </summary>
	public int WorkstationResourceCount = -1;

	/// <summary>
	/// Parallel list of resource names for the breakdown display
	/// (crop names for farmers, animal species for shepherds).
	/// Protobuf-net serialises List&lt;T&gt; cleanly without dictionary quirks.
	/// </summary>
	public List<string> ResourceBreakdownKeys;

	/// <summary>Parallel list of counts matching <see cref="ResourceBreakdownKeys"/>.</summary>
	public List<int> ResourceBreakdownValues;
}
