using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using PKHeX.Core;

namespace SysBot.Pokemon
{
    public sealed class TradePartnerSV
    {
        public uint TID7 { get; }
        public uint SID7 { get; }

        public string TID { get; }
        public string SID { get; }
        public string TrainerName { get; }

        public int Game { get; }
        public int Gender { get; }
        public int Language { get; }

        public TradePartnerSV(TradeMyStatus info)
        {
            TID = info.DisplayTID.ToString("D6");
            SID = info.DisplaySID.ToString("D4");
            TID7 = info.DisplayTID;
            SID7 = info.DisplaySID;
            TrainerName = info.OT;
            Game = info.Game;
            Language = info.Language;
            Gender = info.Gender;

        }
    }

    public sealed class TradeMyStatus
    {
        public readonly byte[] Data = new byte[0x30];

        public uint DisplaySID => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(0)) / 1_000_000;
        public uint DisplayTID => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(0)) % 1_000_000;

        public int Game => Data[4];
        public int Gender => Data[5];
        public int Language => Data[6];

        public string OT => StringConverter8.GetString(Data.AsSpan(8, 24));
    }

}
