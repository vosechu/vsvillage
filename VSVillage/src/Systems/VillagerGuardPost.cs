using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Explicit ProtoMember tags lock the on-disk save format. DO NOT REORDER, DO NOT RENUMBER.
// New fields go at the END with the next free tag.
[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class VillagerGuardPost
{
	[ProtoMember(1)]
	public BlockPos Pos;

	[ProtoMember(2)]
	public long DayOwnerId = -1L;

	[ProtoMember(3)]
	public long NightOwnerId = -1L;

	public long OwnerFor(EnumGuardShift shift)
	{
		return shift switch
		{
			EnumGuardShift.day => DayOwnerId,
			EnumGuardShift.night => NightOwnerId,
			_ => -1L,
		};
	}

	public void SetOwnerFor(EnumGuardShift shift, long ownerId)
	{
		if (shift == EnumGuardShift.day) DayOwnerId = ownerId;
		else if (shift == EnumGuardShift.night) NightOwnerId = ownerId;
	}
}
