using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class VillagerBed
{
	public BlockPos Pos;

	public long OwnerId = -1L;
}
