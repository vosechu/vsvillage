using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Tags 1-3 lock the order the previous `ImplicitFields.AllPublic` mode produced (member
// names sorted alphabetically). DO NOT REORDER, DO NOT RENUMBER; new fields take the next
// free tag at the end. Renumbering silently corrupts every saved village.
[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class VillagerWorkstation
{
	[ProtoMember(1)]
	public long OwnerId = -1L;

	[ProtoMember(2)]
	public BlockPos Pos;

	[ProtoMember(3)]
	public EnumVillagerProfession Profession;

	// Trader workstations only. Null or empty means TraderShopType.Default.
	[ProtoMember(4)]
	public string ShopType;

	// Times the shop type has been paid to change. Drives TraderShopType.CostFor.
	[ProtoMember(5)]
	public int ShopTypeChanges;
}
