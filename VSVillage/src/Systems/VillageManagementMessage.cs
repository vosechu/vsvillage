using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class VillageManagementMessage
{
	public EnumVillageManagementOperation Operation;

	public string Id;

	public string Name;

	public int Radius;

	public BlockPos Pos;

	public long VillagerToRemove;

	public BlockPos StructureToRemove;

	/// <summary>Position of the workstation or bed being assigned (for assignWorkstation / assignBed operations).</summary>
	public BlockPos StructureToAssign;

	public EnumVillagerProfession VillagerProfession;

	public string VillagerType;

	/// <summary>Entity ID of the villager to assign (-1 to clear the assignment).</summary>
	public long AssigneeEntityId = -1L;
}
