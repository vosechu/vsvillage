using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Explicit ProtoMember tags lock the on-disk save format. Tags 1-2 match what
// protobuf-net's previous `ImplicitFields.AllPublic` mode auto-assigned by
// sorting member names ALPHABETICALLY (OwnerId < Pos). DO NOT REORDER, DO NOT
// RENUMBER. New fields go at the END with the next free tag -- adding a field
// in the middle (or letting auto-assignment shift them) breaks every existing
// save the same way the 2026-05-06 incident did.
[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class VillagerBed
{
	[ProtoMember(1)]
	public long OwnerId = -1L;

	[ProtoMember(2)]
	public BlockPos Pos;
}
