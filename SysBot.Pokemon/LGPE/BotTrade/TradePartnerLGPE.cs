using System;
using System.Diagnostics;
using PKHeX.Core;

namespace SysBot.Pokemon
{
    public sealed class TradePartnerLGPE
    {
        public string TID7 { get; }
        public string SID7 { get; }
        public uint TrainerID { get; }
        public string TrainerName { get; }
        public GameVersion GameVer { get; }



        public TradePartnerLGPE(string Name, int TID, int SID, GameVersion Game)
        {
            TID7= TID.ToString();
            SID7 = SID.ToString();
            TrainerName = Name;
            GameVer = Game;
        }
    }
}