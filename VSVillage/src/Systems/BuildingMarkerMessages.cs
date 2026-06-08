using ProtoBuf;
using Vintagestory.API.MathTools;

namespace VsVillage;

// Client to server. Player picked a schematic and hit Begin Build. Server validates village + gear,
// charges, sets BE state, spawns scaffold, enqueues construction.
[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class BuildingMarkerSelectMessage
{
    [ProtoMember(1)] public BlockPos MarkerPos;
    [ProtoMember(2)] public string SelectedId;
    [ProtoMember(3)] public int RotationAngle;
}

// Client to server. Player confirmed cancellation. Server clears volume + refunds 50%.
[ProtoContract(ImplicitFields = ImplicitFields.None)]
public class BuildingMarkerCancelMessage
{
    [ProtoMember(1)] public BlockPos MarkerPos;
}
