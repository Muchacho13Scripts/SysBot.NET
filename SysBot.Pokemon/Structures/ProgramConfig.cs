using SysBot.Base;

namespace SysBot.Pokemon
{
    public class ProgramConfig : BotList<PokeBotState>
    {
        public ProgramMode Mode { get; set; } = ProgramMode.SV;
        public PokeTradeHubConfig Hub { get; set; } = new();
    }

    public enum ProgramMode
    {
        LGPE = 0, // LGPE
        SWSH = 1,
        BDSP = 2,
        LA = 3,
        SV = 4,
    }
}
