using System;
using System.Diagnostics;
using System.Text;
using PKHeX.Core;

namespace SysBot.Pokemon
{
    public sealed class TradePartnerLA
    {
        public uint IDHash { get; }

        public uint TID7 { get; }
        public uint SID7 { get; }

        public string TID { get; }
        public string SID { get; }
        public string TrainerName { get; }

        public byte Game { get; }
        public byte Language { get; }
        public byte Gender { get; }
        public TradePartnerLA(byte[] TIDSID, byte[] trainerNameObject, byte[] idbytes)
        {
            Debug.Assert(TIDSID.Length == 4);
            IDHash = BitConverter.ToUInt32(TIDSID, 0);
            TID7 = (uint)Math.Abs(IDHash % 1_000_000);
            SID7 = (uint)Math.Abs(IDHash / 1_000_000);
            TID = $"{TID7:000000}";
            SID = $"{SID7:0000}";

            Game = idbytes[0];
            Gender = idbytes[1];
            Language = idbytes[3];

            TrainerName = Encoding.Unicode.GetString(trainerNameObject).TrimEnd('\0');
        }

        public const int MaxByteLengthStringObject = 0x26;
    }
}
