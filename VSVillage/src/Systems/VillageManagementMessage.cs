using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Explicit ProtoMember tags lock the wire format between client and server.
// Tags 1-11 match what protobuf-net's previous `ImplicitFields.AllPublic` mode
// auto-assigned by sorting member names ALPHABETICALLY. DO NOT REORDER, DO NOT
// RENUMBER. New fields go at the END with the next free tag -- adding one in
// the middle (or letting auto-assignment re-sort them) silently mis-decodes
// every field on the wire for any mixed-version client/server pair.
[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class VillageManagementMessage
{
	// Entity ID of the villager to assign (-1 to clear the assignment).
	[ProtoMember(1)]
	public long AssigneeEntityId = -1L;

	[ProtoMember(2)]
	public string Id;

	[ProtoMember(3)]
	public string Name;

	[ProtoMember(4)]
	public EnumVillageManagementOperation Operation;

	[ProtoMember(5)]
	public BlockPos Pos;

	[ProtoMember(6)]
	public int Radius;

	// Position of the workstation or bed being assigned (for assignWorkstation / assignBed operations).
	[ProtoMember(7)]
	public BlockPos StructureToAssign;

	[ProtoMember(8)]
	public BlockPos StructureToRemove;

	[ProtoMember(9)]
	public EnumVillagerProfession VillagerProfession;

	[ProtoMember(10)]
	public long VillagerToRemove;

	[ProtoMember(11)]
	public string VillagerType;
}
