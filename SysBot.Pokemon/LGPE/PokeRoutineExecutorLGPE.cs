using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PKHeX.Core;
using SysBot.Base;
using static SysBot.Pokemon.PokeDataOffsetsLGPE;
using static SysBot.Base.SwitchButton;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Drawing;

namespace SysBot.Pokemon
{
    public abstract class PokeRoutineExecutorLGPE : PokeRoutineExecutor<PB7>
    {
        protected const int HidWaitTime = 50;
        protected PokeDataOffsetsLGPE Offsets { get; } = new ();
        protected PokeRoutineExecutorLGPE(PokeBotState cfg) : base(cfg)
        {
        }

        public async Task<PB7> LGReadPokemon(uint offset, CancellationToken token, int size = EncryptedSize, bool heap = true)
        {
            byte[] data;
            if (heap == true)
                data = await Connection.ReadBytesAsync(offset, size, token).ConfigureAwait(false);
            else
                data = await SwitchConnection.ReadBytesMainAsync(offset, size, token).ConfigureAwait(false);
            return new PB7(data);
        }

        public override async Task<PB7> ReadPokemon(ulong offset, int size, CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesAbsoluteAsync(offset, size, token).ConfigureAwait(false);
            return new PB7(data);
        }

        public override async Task<PB7> ReadPokemonPointer(IEnumerable<long> jumps, int size, CancellationToken token)
        {
            var (valid, offset) = await ValidatePointerAll(jumps, token).ConfigureAwait(false);
            if (!valid)
                return new PB7();
            return await ReadPokemon(offset, token).ConfigureAwait(false);
        }

        public override async Task<PB7> ReadPokemon(ulong offset, CancellationToken token) => await ReadPokemon(offset, 260, token).ConfigureAwait(false);

        public override async Task<PB7> ReadBoxPokemon(int box, int slot, CancellationToken token)
        {
            // Shouldn't be reading anything but box1slot1 here. Slots are not consecutive.
           long[] BoxStartPokemonPointer = new long[] { 0x533675B0, 260, 380 };
            return await ReadPokemonPointer(BoxStartPokemonPointer, 260, token).ConfigureAwait(false);
        }

        public async Task<PB7?> ReadUntilPresent(uint offset, int waitms, int waitInterval, CancellationToken token, int size = EncryptedSize, bool heap = true)
        {
            int msWaited = 0;
            while (msWaited < waitms)
            {
                var pk = await LGReadPokemon(offset, token, size, heap).ConfigureAwait(false);
                if (pk.Species != 0 && pk.ChecksumValid)
                    return pk;
                await Task.Delay(waitInterval, token).ConfigureAwait(false);
                msWaited += waitInterval;
            }
            return null;
        }

        public async Task SetBoxPokemonAbsolute(int boxi, int sloti, PB7 pkm, CancellationToken token, ITrainerInfo? sav = null)
        {
            if (sav != null)
            {
                // Update PKM to the current save's handler data
                pkm.Trade(sav);
                pkm.RefreshChecksum();
            }

            pkm.ResetPartyStats();
            var dStoredLength = 260 - 0x1C;
            uint GetBoxOffset(int box) => 0x533675B0;
            uint GetSlotOffset(int box, int slot) => GetBoxOffset(box) + (uint)((260 + 380) * slot);
            var dslotofs = GetSlotOffset(boxi, sloti);
            await Connection.WriteBytesAsync(pkm.EncryptedBoxData.Slice(0, dStoredLength), BoxSlot1, token);
            await Connection.WriteBytesAsync(pkm.EncryptedBoxData.AsSpan(dStoredLength).ToArray(), (uint)(dslotofs + dStoredLength + 0x70), token);
        }

        public async Task<SAV7b> IdentifyTrainer(CancellationToken token)
        {
            // Check title so we can warn if mode is incorrect.
            string title = await SwitchConnection.GetTitleID(token).ConfigureAwait(false);
            if (title != LetsGoPikachuID && title != LetsGoEeveeID)
                throw new Exception($"{title} is not a valid Let's Go title. Is your mode correct?");

            var sav = await GetFakeTrainerSAV(token).ConfigureAwait(false);
            InitSaveData(sav);

            if (!IsValidTrainerData())
                throw new Exception("Trainer data is not valid. Refer to the SysBot.NET wiki for bad or no trainer data.");

            return sav;
        }

        public async Task<SAV7b> GetFakeTrainerSAV(CancellationToken token)
        {
         
                SAV7b lgpe = new SAV7b();

                byte[] dest = lgpe.Blocks.Status.Data;
                int startofs = lgpe.Blocks.Status.Offset;
                byte[]? data = await Connection.ReadBytesAsync(TrainerData, TrainerSize, token).ConfigureAwait(false);
                data.CopyTo(dest, startofs);
                return lgpe;
         }

            public async Task InitializeHardware(IBotStateSettings settings, CancellationToken token)
        {
            Log("Detaching on startup.");
            await DetachController(token).ConfigureAwait(false);
            Log("Setup the Controller.");
            await SetController(token).ConfigureAwait(false);
            if (settings.ScreenOff)
            {
                Log("Turning off screen.");
                await SetScreen(ScreenState.Off, token).ConfigureAwait(false);
            }
        }

        public async Task SetController(CancellationToken token)
        {
            var cmd = SwitchCommand.Configure(SwitchConfigureParameter.controllerType, 1);
            await Connection.SendAsync(cmd, token).ConfigureAwait(false);
        }

        public async Task<TradePartnerLGPE> FetchIDFromOffset(SAV7b sav, CancellationToken token)
        {
            var tradepartnersav = new SAV7b();
            var tradepartnersav2 = new SAV7b();
            string name;
            int TID;
            int SID;
            GameVersion Game = new();
            var tpsarray = await SwitchConnection.ReadBytesAsync(TradePartnerData, 0x168, token);
            tpsarray.CopyTo(tradepartnersav.Blocks.Status.Data, tradepartnersav.Blocks.Status.Offset);
            var tpsarray2 = await SwitchConnection.ReadBytesAsync(TradePartnerData2, 0x168, token);
            tpsarray2.CopyTo(tradepartnersav2.Blocks.Status.Data, tradepartnersav2.Blocks.Status.Offset);
            if (tradepartnersav.OT != sav.OT)
            {
                name = tradepartnersav.OT;
                TID = (int)tradepartnersav.DisplayTID;
                SID = (int)tradepartnersav.DisplaySID;
                Game = (GameVersion)tradepartnersav.Game;
                Log($"Found Link Trade Parter: {tradepartnersav.OT}, TID: {tradepartnersav.DisplayTID}, SID: {tradepartnersav.DisplaySID},Game: {(GameVersion)tradepartnersav.Game}");
           }
            else
            {
                name = tradepartnersav2.OT;
                TID = (int)tradepartnersav2.DisplayTID;
                SID = (int)tradepartnersav2.DisplaySID;
                Game = (GameVersion)tradepartnersav2.Game;
                Log($"Found Link Trade Parter: {tradepartnersav2.OT}, TID: {tradepartnersav2.DisplayTID}, SID: {tradepartnersav2.DisplaySID}");
           }


            return new TradePartnerLGPE(name, TID, SID, Game);
        }

        public async Task<ulong> ParsePointer(String pointer, CancellationToken token)
        {
            var ptr = pointer;
            uint finadd = 0;
            if (!ptr.EndsWith("]"))
                finadd = Util.GetHexValue(ptr.Split('+').Last());
            var jumps = ptr.Replace("main", "").Replace("[", "").Replace("]", "").Split(new[] { "+" }, StringSplitOptions.RemoveEmptyEntries);
            if (jumps.Length == 0)
            {
                Log("Invalid Pointer");
                return 0;
            }

            var initaddress = Util.GetHexValue(jumps[0].Trim());
            ulong address = BitConverter.ToUInt64(await SwitchConnection.ReadBytesMainAsync(initaddress, 0x8, token).ConfigureAwait(false), 0);
            foreach (var j in jumps)
            {
                var val = Util.GetHexValue(j.Trim());
                if (val == initaddress)
                    continue;
                if (val == finadd)
                {
                    address += val;
                    break;
                }
                address = BitConverter.ToUInt64(await SwitchConnection.ReadBytesAbsoluteAsync(address + val, 0x8, token).ConfigureAwait(false), 0);
            }
            return address;
        }

        public bool IsPKLegendary(int species) => Enum.IsDefined(typeof(Legendary), (Legendary)species);

        public async Task<bool> LGIsInTitleScreen(CancellationToken token) => !((await SwitchConnection.ReadBytesMainAsync(IsInTitleScreen, 1, token).ConfigureAwait(false))[0] == 1);
        public async Task<bool> LGIsInBattle(CancellationToken token) => (await SwitchConnection.ReadBytesMainAsync(IsInBattleScenario, 1, token).ConfigureAwait(false))[0] > 0;
        public async Task<bool> LGIsInCatchScreen(CancellationToken token) => (await SwitchConnection.ReadBytesMainAsync(IsInOverworld, 1, token).ConfigureAwait(false))[0] != 0;
        public async Task<bool> LGIsinwaitingScreen(CancellationToken token) => BitConverter.ToUInt32(await SwitchConnection.ReadBytesMainAsync(waitingscreen, 4, token).ConfigureAwait(false), 0) == 0;
        public async Task<bool> LGTradeButtonswait(CancellationToken token) => string.Format("0x{0:X8}", BitConverter.ToUInt32(await SwitchConnection.ReadBytesMainAsync(tradebuttons, 4, token), 0)) == "0x00000000";
        public async Task<bool> LGIsInTrade(CancellationToken token) => (await SwitchConnection.ReadBytesMainAsync(IsInTrade, 1, token).ConfigureAwait(false))[0] != 0;
        public async Task<bool> LGIsGiftFound(CancellationToken token) => (await SwitchConnection.ReadBytesMainAsync(IsGiftFound, 1, token).ConfigureAwait(false))[0] > 0;
        public async Task<uint> LGEncounteredWild(CancellationToken token) => BitConverter.ToUInt16(await Connection.ReadBytesAsync(CatchingSpecies, 2, token).ConfigureAwait(false), 0);
        public async Task<GameVersion> LGWhichGameVersion(CancellationToken token)
        {
            byte[] data = await Connection.ReadBytesAsync(LGGameVersion, 1, token).ConfigureAwait(false);
            if (data[0] == 0x01)
                return GameVersion.GP;
            else if (data[0] == 0x02)
                return GameVersion.GE;
            else
                return GameVersion.Invalid;
        }

        public async Task<bool> LGIsNatureTellerEnabled(CancellationToken token) => (await Connection.ReadBytesAsync(NatureTellerEnabled, 1, token).ConfigureAwait(false))[0] == 0x04;
        public async Task<Nature> LGReadWildNature(CancellationToken token) => (Nature)BitConverter.ToUInt16(await Connection.ReadBytesAsync(WildNature, 2, token).ConfigureAwait(false), 0);
        public async Task LGEnableNatureTeller(CancellationToken token) => await Connection.WriteBytesAsync(BitConverter.GetBytes(0x04), NatureTellerEnabled, token).ConfigureAwait(false);
        public async Task LGEditWildNature(Nature target, CancellationToken token) => await Connection.WriteBytesAsync(BitConverter.GetBytes((uint)target), WildNature, token).ConfigureAwait(false);
        public async Task<uint> LGReadSpeciesCombo(CancellationToken token) =>
            BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(await ParsePointer(SpeciesComboPointer, token).ConfigureAwait(false), 2, token).ConfigureAwait(false), 0);
        public async Task<uint> LGReadComboCount(CancellationToken token) =>
            BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(await ParsePointer(CatchComboPointer, token).ConfigureAwait(false), 2, token).ConfigureAwait(false), 0);
        public async Task LGEditSpeciesCombo(uint species, CancellationToken token) =>
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(species), await ParsePointer(SpeciesComboPointer, token).ConfigureAwait(false), token).ConfigureAwait(false);
        public async Task LGEditComboCount(uint count, CancellationToken token) =>
            await SwitchConnection.WriteBytesAbsoluteAsync(BitConverter.GetBytes(count), await ParsePointer(CatchComboPointer, token).ConfigureAwait(false), token).ConfigureAwait(false);

        public async Task<bool> IsTrue(IEnumerable<long> jumps, CancellationToken token)
        {
            var data = await SwitchConnection.PointerPeek(1, jumps, token).ConfigureAwait(false);
            return data[0] == 1;
        }

        public async Task CleanExit(IBotStateSettings settings, CancellationToken token)
        {
            if (settings.ScreenOff)
            {
                Log("Turning on screen.");
                await SetScreen(ScreenState.On, token).ConfigureAwait(false);
            }
            Log("Detaching controllers on routine exit.");
            await DetachController(token).ConfigureAwait(false);
        }

        protected virtual async Task EnterLinkCode(int code, PokeTradeHubConfig config, CancellationToken token)
        {
            // Default implementation to just press directional arrows. Can do via Hid keys, but users are slower than bots at even the default code entry.
     //       var keys = TradeUtil.GetPresses(code);
     //       foreach (var key in keys)
     //       {
     //           int delay = config.Timings.KeypressTime;
     //           await Click(key, delay, token).ConfigureAwait(false);
     //       }
            // Confirm Code outside of this method (allow synchronization)
            char[] codeChars = $"{code:000}".ToCharArray();
            Log($"Entering Link Trade code: {code}...");
            foreach (int cod in codeChars)
            {
                int pc = cod - '0'; //converting to int
                Log($"Entering Link Trade code: {pc}...");
                if (pc > 4)
                {
                    await SetStick(SwitchStick.RIGHT, 0, -30000, 100, token).ConfigureAwait(false);
                    await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
                }
                if (pc <= 4)
                {
                    for (int i = pc; i > 0; i--)
                    {
                        await SetStick(SwitchStick.RIGHT, 30000, 0, 100, token).ConfigureAwait(false);
                        await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }
                else
                {
                    for (int i = pc - 5; i > 0; i--)
                    {
                        await SetStick(SwitchStick.RIGHT, 30000, 0, 100, token).ConfigureAwait(false);
                        await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }
                await Click(A, 200, token).ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
                if (pc <= 4)
                {
                    for (int i = pc; i > 0; i--)
                    {
                        await SetStick(SwitchStick.RIGHT, -30000, 0, 100, token).ConfigureAwait(false);
                        await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }
                else
                {
                    for (int i = pc - 5; i > 0; i--)
                    {
                        await SetStick(SwitchStick.RIGHT, -30000, 0, 100, token).ConfigureAwait(false);
                        await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }

                if (pc > 4)
                {
                    await SetStick(SwitchStick.RIGHT, 0, 30000, 100, token).ConfigureAwait(false);
                    await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
                }
            }

        }

        public async Task ReOpenGame(PokeTradeHubConfig config, CancellationToken token)
        {
            Log("Error detected, restarting the game!!");
            await CloseGame(config, token).ConfigureAwait(false);
            await StartGame(config, token).ConfigureAwait(false);
        }


        public async Task<bool> CheckIfSoftBanned(ulong offset, CancellationToken token)
        {
            var data = await SwitchConnection.ReadBytesAbsoluteAsync(offset, 4, token).ConfigureAwait(false);
            return BitConverter.ToUInt32(data, 0) != 0;
        }

        public async Task CloseGame(PokeTradeHubConfig config, CancellationToken token)
        {
            var timing = config.Timings;
            // Close out of the game
            await Click(HOME, 2_000 + timing.ExtraTimeReturnHome, token).ConfigureAwait(false);
            await Click(X, 1_000, token).ConfigureAwait(false);
            await Click(A, 5_000 + timing.ExtraTimeCloseGame, token).ConfigureAwait(false);
            Log("Closed out of the game!");
        }

        public async Task StartGame(PokeTradeHubConfig config, CancellationToken token)
        {
            // Open game.
            await Click(A, 1_000 + config.Timings.ExtraTimeLoadProfile, token).ConfigureAwait(false);

            //  The user can optionally turn on the setting if they know of a breaking system update incoming.
            if (config.Timings.AvoidSystemUpdate)
            {
                await Click(DUP, 0_600, token).ConfigureAwait(false);
                await Click(A, 1_000 + config.Timings.ExtraTimeLoadProfile, token).ConfigureAwait(false);
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            while (await LGIsInTitleScreen(token).ConfigureAwait(false))
            {
                if (stopwatch.ElapsedMilliseconds > 6000)
                    await DetachController(token).ConfigureAwait(false);
                await Click(A, 0_500, token).ConfigureAwait(false);
            }
            Log("Game started.");
        }

        public async Task StartGameSaveRecovery(PokeTradeHubConfig config, CancellationToken token)
        {
            //move to JKSV and restore Save when Softbanned
            await Click(DDOWN, 0_600, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_600, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_600, token).ConfigureAwait(false);
            await Click(DRIGHT, 0_600, token).ConfigureAwait(false);
            await Click(A, 2_600, token).ConfigureAwait(false);
            await Click(A, 0_600, token).ConfigureAwait(false); 
            await Click(A, 0_600, token).ConfigureAwait(false);
            await Click(DDOWN, 0_600, token).ConfigureAwait(false);
            await Click(Y, 0_600, token).ConfigureAwait(false);
            await Click(A, 3_600, token).ConfigureAwait(false);
            await Click(B, 0_600, token).ConfigureAwait(false);
            await Click(HOME, 2_600, token).ConfigureAwait(false);

            // Open game.
            await Click(A, 1_000 + config.Timings.ExtraTimeLoadProfile, token).ConfigureAwait(false);

            //  The user can optionally turn on the setting if they know of a breaking system update incoming.
            if (config.Timings.AvoidSystemUpdate)
            {
                await Click(DUP, 0_600, token).ConfigureAwait(false);
                await Click(A, 1_000 + config.Timings.ExtraTimeLoadProfile, token).ConfigureAwait(false);
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            while (await LGIsInTitleScreen(token).ConfigureAwait(false))
            {
                if (stopwatch.ElapsedMilliseconds > 6000)
                    await DetachController(token).ConfigureAwait(false);
                await Click(A, 0_500, token).ConfigureAwait(false);
            }
            Log("Game started.");
        }


        //   public async Task<ulong> GetTradePartnerNID(CancellationToken token) => BitConverter.ToUInt64(await SwitchConnection.PointerPeek(sizeof(ulong), Offsets.LinkTradePartnerNIDPointer, token).ConfigureAwait(false), 0);


    }
}