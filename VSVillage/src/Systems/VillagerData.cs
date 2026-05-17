using ProtoBuf;

namespace VsVillage;

// Explicit ProtoMember tags lock the on-disk save format. Tags 1-3 match what
// protobuf-net's previous `ImplicitFields.AllPublic` mode auto-assigned by
// sorting member names ALPHABETICALLY (Id < Name < Profession). DO NOT REORDER,
// DO NOT RENUMBER. New fields go at the END with the next free tag.
[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class VillagerData
{
	[ProtoMember(1)]
	public long Id;

	[ProtoMember(2)]
	public string Name;

	[ProtoMember(3)]
	public EnumVillagerProfession Profession;
}
