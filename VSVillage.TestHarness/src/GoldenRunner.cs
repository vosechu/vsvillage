using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VsVillageTest;

public class GoldenRunner : ModSystem
{
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.ChatCommands.Create("vsvillage:test")
            .WithDescription("VS Village behavioral test suites (fork-only)")
            .RequiresPrivilege(Privilege.gamemode)
            .BeginSubCommand("list")
                .WithDescription("List suites and scenario counts")
                .HandleWith(_ => TextCommandResult.Success("(no suites)"))
            .EndSubCommand();
    }
}
