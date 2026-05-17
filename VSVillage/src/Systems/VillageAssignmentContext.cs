using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Sent server-to-client when a player right-clicks a workstation or bed that
// belongs to a village. The client uses this to open AssignVillagerGui.
// Explicit ProtoMember tags lock the wire format. Tags 1-6 match what
// protobuf-net's previous `ImplicitFields.AllPublic` mode auto-assigned by
// sorting member names ALPHABETICALLY. DO NOT REORDER, DO NOT RENUMBER. New
// fields go at the END with the next free tag.
[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class VillageAssignmentContext
{
	// True when the clicked block is a bed; false when it's a workstation.
	[ProtoMember(1)]
	public bool IsBed;

	// Parallel list of resource names for the breakdown display
	// (crop names for farmers, animal species for shepherds).
	// Protobuf-net serialises List&lt;T&gt; cleanly without dictionary quirks.
	[ProtoMember(2)]
	public List<string> ResourceBreakdownKeys;

	// Parallel list of counts matching ResourceBreakdownKeys.
	[ProtoMember(3)]
	public List<int> ResourceBreakdownValues;

	// World position of the clicked workstation or bed.
	[ProtoMember(4)]
	public BlockPos StructurePos;

	// Full village data -- contains VillagerSaveData, Workstations, Beds, etc.
	[ProtoMember(5)]
	public Village Village;

	// Total resource count relevant to this workstation's profession:
	// farmland blocks for farmers, enclosed animals for shepherds.
	// -1 means no stats available (other professions, or beds).
	[ProtoMember(6)]
	public int WorkstationResourceCount = -1;
}
