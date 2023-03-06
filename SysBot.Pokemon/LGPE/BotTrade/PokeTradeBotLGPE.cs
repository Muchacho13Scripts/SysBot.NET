using PKHeX.Core;
using PKHeX.Core.Searching;
using System.Drawing;
using SysBot.Base;
using System;
using PKHeX.Drawing;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsetsLGPE;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.NetworkInformation;

namespace SysBot.Pokemon
{
    // ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
    public class PokeTradeBotLGPE : PokeRoutineExecutorLGPE, ICountBot
    {
        private readonly PokeTradeHub<PB7> Hub;
        private readonly TradeSettings TradeSettings;
        private readonly TradeAbuseSettings AbuseSettings;

        public ICountSettings Counts => TradeSettings;

        private static readonly TrackedUserLog PreviousUsers = new();
        private static readonly TrackedUserLog PreviousUsersDistribution = new();
        private static readonly TrackedUserLog EncounteredUsers = new();

        /// <summary>
        /// Folder to dump received trade data to.
        /// </summary>
        /// <remarks>If null, will skip dumping.</remarks>
        private readonly IDumper DumpSetting;

        /// <summary>
        /// Synchronized start for multiple bots.
        /// </summary>
        public bool ShouldWaitAtBarrier { get; private set; }

        /// <summary>
        /// Tracks failed synchronized starts to attempt to re-sync.
        /// </summary>
        public int FailedBarrier { get; private set; }

        public PokeTradeBotLGPE(PokeTradeHub<PB7> hub, PokeBotState cfg) : base(cfg)
        {
            Hub = hub;
            TradeSettings = hub.Config.Trade;
            AbuseSettings = hub.Config.TradeAbuse;
            DumpSetting = hub.Config.Folder;
            lastOffered = new byte[8];
        }

        // Cached offsets that stay the same per session.
        private ulong BoxStartOffset;
        private ulong UnionGamingOffset;
        private ulong UnionTalkingOffset;
        private ulong SoftBanOffset;
        private int TradeCount = 0;


        // Count up how many trades we did without rebooting.
        private int sessionTradeCount;
        // Track the last Pokémon we were offered since it persists between trades.
        private byte[] lastOffered;

        public override async Task MainLoop(CancellationToken token)
        {
            try
            {
                await InitializeHardware(Hub.Config.Trade, token).ConfigureAwait(false);

                Log("Identifying trainer data of the host console.");
                var sav = await IdentifyTrainer(token).ConfigureAwait(false);

                Log($"Starting main {nameof(PokeTradeBotBS)} loop.");
                await InnerLoop(sav, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }

            Log($"Ending {nameof(PokeTradeBotBS)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        public override async Task HardStop()
        {
            UpdateBarrier(false);
            await CleanExit(TradeSettings, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task InnerLoop(SAV7b sav, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Config.IterateNextRoutine();
                var task = Config.CurrentRoutineType switch
                {
                    PokeRoutineType.Idle => DoNothing(token),
                    _ => DoTrades(sav, token),
                };
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    Log(e.Message);
                    Connection.Reset();
                }
            }
        }

        private async Task DoNothing(CancellationToken token)
        {
            int waitCounter = 0;
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
            {
                if (waitCounter == 0)
                    Log("No task assigned. Waiting for new task assignment.");
                waitCounter++;
                if (waitCounter % 10 == 0 && Hub.Config.AntiIdle)
                    await Click(B, 1_000, token).ConfigureAwait(false);
                else
                    await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }

        private async Task DoTrades(SAV7b sav, CancellationToken token)
        {
            var type = Config.CurrentRoutineType;
            int waitCounter = 0;
            var read = await SwitchConnection.ReadBytesMainAsync(ScreenOff, 1, token);
            overworld = read[0];
            while (!token.IsCancellationRequested && Config.NextRoutineType == type)
            {
                var (detail, priority) = GetTradeData(type);
                if (detail is null)
                {
                    await WaitForQueueStep(waitCounter++, token).ConfigureAwait(false);
                    continue;
                }
                waitCounter = 0;

                detail.IsProcessing = true;
                string tradetype = $" ({detail.Type})";
                Log($"Starting next {type}{tradetype} Bot Trade. Getting data...");
                await Task.Delay(500, token).ConfigureAwait(false);
                Hub.Config.Stream.StartTrade(this, detail, Hub);
                Hub.Queues.StartTrade(this, detail);

                await PerformTrade(sav, detail, type, priority, token).ConfigureAwait(false);
            }
        }

        private async Task WaitForQueueStep(int waitCounter, CancellationToken token)
        {
            if (waitCounter == 0)
            {
                // Updates the assets.
                Hub.Config.Stream.IdleAssets(this);
                Log("Nothing to check, waiting for new users...");
            }

            const int interval = 10;
            if (waitCounter % interval == interval - 1 && Hub.Config.AntiIdle)
                await Click(B, 1_000, token).ConfigureAwait(false);
            else
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        protected virtual (PokeTradeDetail<PB7>? detail, uint priority) GetTradeData(PokeRoutineType type)
        {
            if (Hub.Queues.TryDequeue(type, out var detail, out var priority))
                return (detail, priority);
            if (Hub.Queues.TryDequeueLedy(out detail))
                return (detail, PokeTradePriorities.TierFree);
            return (null, PokeTradePriorities.TierFree);
        }

        private async Task PerformTrade(SAV7b sav, PokeTradeDetail<PB7> detail, PokeRoutineType type, uint priority, CancellationToken token)
        {
            PokeTradeResult result;
            try
            {
                result = await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);
                if (result == PokeTradeResult.Success)
                    return;
            }
            catch (SocketException socket)
            {
                Log(socket.Message);
                result = PokeTradeResult.ExceptionConnection;
                HandleAbortedTrade(detail, type, priority, result);
                throw; // let this interrupt the trade loop. re-entering the trade loop will recheck the connection.
            }
            catch (Exception e)
            {
                Log(e.Message);
                result = PokeTradeResult.ExceptionInternal;
            }

            HandleAbortedTrade(detail, type, priority, result);
        }

        private void HandleAbortedTrade(PokeTradeDetail<PB7> detail, PokeRoutineType type, uint priority, PokeTradeResult result)
        {
            detail.IsProcessing = false;
            if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
            {
                detail.IsRetry = true;
                Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
                detail.SendNotification(this, "Oops! Something happened. I'll requeue you for another attempt.");
            }
            else
            {
                detail.SendNotification(this, $"Oops! Something happened. Canceling the trade: {result}.");
                detail.TradeCanceled(this, result);
            }
        }

        private void DistributionCounter()
        {
            // If the file can be opened for exclusive access it means that the file
            // is no longer locked by another process.
            try
            {
                using (FileStream inputStream = File.Open($"{DumpSetting.DumpFolder}" + $"\\TotalDistributed.txt", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    string justnumbers = File.ReadAllText($"{DumpSetting.DumpFolder}" + $"\\TotalDistributed.txt");
                    int readcount = Int32.Parse(string.Concat(justnumbers.Where(char.IsDigit)));
                    File.WriteAllText($"{DumpSetting.DumpFolder}" + $"\\TotalDistributed.txt", $"Total distributed Pokémon: {readcount + 1}");
                }
            }
            catch (Exception) {   }
        }

        private void SetText(string text)
        {
            File.WriteAllText($"LinkCode_{Connection.Name}.txt", text);
        }

        private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV7b sav, PokeTradeDetail<PB7> poke, CancellationToken token)
        {
            sessionTradeCount++;
            TradeCount++;
            if (TradeCount > 20)
            {
                await CloseGame(Hub.Config, token).ConfigureAwait(false);
                await StartGameSaveRecovery(Hub.Config, token).ConfigureAwait(false);
                await Task.Delay(5_000);
                TradeCount = 0;
            }
            Log($"Starting trade #{sessionTradeCount} for this session.");
            // Update Barrier Settings
            UpdateBarrier(poke.IsSynchronized);
            poke.TradeInitialize(this);
            Hub.Config.Stream.EndEnterCode(this);
            Stopwatch btimeout = new();
            var BoxStart = BoxSlot1;
            var SlotSize = 260;
            var GapSize = 380;
            var SlotCount = 25;
            var read = await SwitchConnection.ReadBytesMainAsync(ScreenOff, 1, token);
            uint GetBoxOffset(int box) => 0x533675B0;
            uint GetSlotOffset(int box, int slot) => GetBoxOffset(box) + (uint)((SlotSize + GapSize) * slot);
            var lastOffer = new PB7(await SwitchConnection.ReadBytesAsync(OfferedPokemon, 0x104, token).ConfigureAwait(false));

            var toSend = poke.TradeData;
            if (toSend.Species != 0)
                await SetBoxPokemonAbsolute(1, 0, toSend, token, sav).ConfigureAwait(false);

            var egg = String.Empty;
            if (poke.TradeData.IsEgg)
                egg = " (Egg)";
            var shiny = String.Empty;
            if (poke.TradeData.IsShiny)
                shiny = "Shiny ";
            if (poke.Type == PokeTradeType.Random)
            {
                SetText($"Sending: " + shiny + $"{(Species)poke.TradeData.Species}" + egg);
                char[] codeChars = $"{poke.Code:000}".ToCharArray();
                Log($"Generating Distribution ImageFile for Code {codeChars[0]}-{codeChars[1]}-{codeChars[2]}");
                var code0 = Image.FromFile($"code{codeChars[0]}.png");
                var code1 = Image.FromFile($"code{codeChars[1]}.png");
                var code2 = Image.FromFile($"code{codeChars[2]}.png");
                var finalpic = Merge(code0, code1, code2);
                finalpic.Save($"LinkCodeImage_{Connection.Name}.png");

            }
            else
                SetText($"Trade Request for {poke.Trainer.TrainerName}\r\nSending: " + shiny + $"{(Species)poke.TradeData.Species}" + egg);

            if (!await EnterUnionRoomWithCode(poke.Type, poke.Code, read, token).ConfigureAwait(false))
            {
                // We don't know how far we made it in, so restart the game to be safe.
                await RestartGameLGPE(token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }
            poke.TradeSearching(this);
            //waiting for Trainer
            await Task.Delay(3000);
            btimeout.Restart();
            var nofind = false;
            while (await LGIsinwaitingScreen(token))
            {
                await Task.Delay(1_000);
                //if time spent searching is greater then the hub time + 10 seconds then assume user not found and back out
                //recommended wait time is 45 seconds for LGPE - give or take depending on network speeds etc.
                Log($"waitingscreen for Trainer for {btimeout.ElapsedMilliseconds} milliseconds");
                if (btimeout.ElapsedMilliseconds >= (Hub.Config.Trade.TradeWaitTime * 1_000 + 10_000))
                {
                    await Click(B, 1000, token);
                    nofind = true;
                    read = await SwitchConnection.ReadBytesMainAsync(ScreenOff, 1, token);
                    while (read[0] != overworld)
                    {
                        await Click(B, 1000, token);
                        read = await SwitchConnection.ReadBytesMainAsync(ScreenOff, 1, token);
                    }
                    await Click(B, 1000, token);
                    await Click(B, 1000, token);
                    await Click(B, 1000, token);
                    await Click(B, 1000, token);
                }
            }
            Hub.Config.Stream.EndEnterCode(this);
            if (nofind)
            {
                await Click(B, 1000, token);
                Log("User not found");
                return PokeTradeResult.NoTrainerFound;

            }
            await Task.Delay(5_000, token).ConfigureAwait(false);
            var tradePartner = await FetchIDFromOffset(sav, token).ConfigureAwait(false);
            RecordUtil<PokeTradeBot>.Record($"Initiating\t{tradePartner.TrainerName}\t{poke.Trainer.TrainerName}\t{poke.Trainer.ID}\t{poke.ID}\t{toSend.EncryptionConstant:X8}");
            await Task.Delay(2_000, token).ConfigureAwait(false);

            poke.SendNotification(this, $"Found Link Trade partner: {tradePartner.TrainerName}. Waiting for a Pokémon...");
            Log($"Found trading partner: {tradePartner.TrainerName}-{tradePartner.SID7}-{tradePartner.TID7} ({poke.Trainer.TrainerName})");
            string partnername = tradePartner.TrainerName;
            // Requires at least one trade for this pointer to make sense, so cache it here.
            SetText($"Trade Partner found: {tradePartner.TrainerName}\r\nTrading now...");
            if (poke.Type == PokeTradeType.Random)
            {
                if (File.Exists($"LinkCodeImage_{Connection.Name}.png"))
                    File.Delete($"LinkCodeImage_{Connection.Name}.png");
            }

            if (poke.Type == PokeTradeType.Dump)
                return await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);

            int waittime = 30_000;

            List<PB7> ls = new List<PB7>();
            if (poke.TradeData.OT_Name == "pokedex" && poke.TradeData.TrainerSID7 == 1337)
            {
                waittime = 375_000;
                string directory = Path.Combine(poke.TradeData.OT_Name, poke.TradeData.Nickname);
                string[] fileEntries = Directory.GetFiles(directory);
                foreach (string fileName in fileEntries)
                {
                    var data = File.ReadAllBytes(fileName);
                    var pkt = EntityFormat.GetFromBytes(data);
                    pkt.RefreshChecksum();
                    var pk2 = EntityConverter.ConvertToType(pkt, typeof(PB7), out _) as PB7;
                    ls.Add(pk2);
                }
            }
            else if (poke.TradeData.OT_Name == "multitrade" && poke.TradeData.TrainerSID7 == 1338)
            {
                waittime = 200_000;
                string directory = Path.Combine(poke.TradeData.OT_Name, poke.Trainer.TrainerName);
                string[] fileEntries = Directory.GetFiles(directory);
                foreach (string fileName in fileEntries)
                {
                    var data = File.ReadAllBytes(fileName);
                    var pkt = EntityFormat.GetFromBytes(data);
                    pkt.RefreshChecksum();
                    var pk2 = EntityConverter.ConvertToType(pkt, typeof(PB7), out _) as PB7;
                    ls.Add(pk2);
                }
                Directory.Delete(directory, true);
            }
            else
            {
                ls.Add(toSend);
            }

            int counting = 0;
            foreach (var send in ls)
            {
                counting++;
                toSend = send;

                // set b1s1



                Log("Wait for user input... Needs to be different from the previously offered Pokémon.");
                Log($"Previous Pokemon: {(Species)lastOffer.Species}, TName: {lastOffer.OT_Name}, TID: {lastOffer.TrainerTID7}, Language: {lastOffer.Language}, OTGender: {lastOffer.OT_Gender}\r\nEncryption: {lastOffer.EncryptionConstant}");
                var tradeOffered2 = await ReadUntilChanged2(lastOffer, waittime, 1_000, token).ConfigureAwait(false);
              //  var tradeOffered = await ReadUntilChanged(LinkTradePokemonOffset, lastOffered, waittime, 1_000, false, true, token).ConfigureAwait(false);
               // var offere2 = await ReadUntilPresentPointer(Offsets.LinkTradePartnerPokemonPointer, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);


                if (!tradeOffered2)
                {
                    if (waittime > 199_000)
                        System.IO.File.WriteAllText($"DexTradeError.txt", $"Dex Trade stopped early. Last Entry was: {(Species)toSend.Species}, Trade Nr.:{counting}/{ls.Count()}");
                    Log("Takes too long to offer something! Ending trade");
                    return PokeTradeResult.TrainerTooSlow;
                }

                // If we detected a change, they offered something.
                var offered = new PB7(await SwitchConnection.ReadBytesAsync(OfferedPokemon, 0x104, token).ConfigureAwait(false));

                Log($"New Offered Pokemon: {(Species)offered.Species}, TName: {offered.OT_Name}, TID: {offered.TrainerTID7}, Language: {offered.Language}, OTGender: {offered.OT_Gender}");


                // Changing OT for Subs
                if (toSend.OT_Name == "sub")
                {
                    Log($"Changing OT to Partner OT");
                    await SetBoxPkmWithSwappedIDDetailsLGPE(send, offered, sav, tradePartner, token);
                    await Task.Delay(2_500, token).ConfigureAwait(false);
                }


                Log($"New Sending Pokemon: {(Species)toSend.Species}, TName: {toSend.OT_Name}, TID: {toSend.TrainerTID7}");

                lastOffered = await SwitchConnection.ReadBytesAsync(OfferedPokemon, 0x104, token);
                lastOffer = await LGReadPokemon(BoxSlot1, token).ConfigureAwait(false);

                PokeTradeResult update;
            var trainer = new PartnerDataHolder(0, tradePartner.TrainerName, tradePartner.TID7);
            (toSend, update) = await GetEntityToSend(sav, poke, offered, toSend, trainer, token).ConfigureAwait(false);
            if (update != PokeTradeResult.Success)
                return update;

            var tradeResult = await ConfirmAndStartTrading(poke, token).ConfigureAwait(false);
            if (tradeResult != PokeTradeResult.Success)
                return tradeResult;

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;


                var receivedNext = await LGReadPokemon(BoxSlot1, token).ConfigureAwait(false);
                // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
                if (SearchUtil.HashByDetails(receivedNext) == SearchUtil.HashByDetails(toSend))
                {
                    Log("User did not complete the trade.");
                    return PokeTradeResult.TrainerTooSlow;
                }

                poke.SendNotification(this, receivedNext, $"You sent me {(Species)receivedNext.Species} for {(Species)toSend.Species}!");



            }
            // Trade was Successful!
            var received = await LGReadPokemon(BoxSlot1, token).ConfigureAwait(false);
            // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
            if (SearchUtil.HashByDetails(received) == SearchUtil.HashByDetails(toSend) && received.Checksum == toSend.Checksum)
            {
                Log("User did not complete the trade.");
                return PokeTradeResult.TrainerTooSlow;
            }
            SetText($"Trade completed!\r\nNew Code incoming...");
            // As long as we got rid of our inject in b1s1, assume the trade went through.
            Log("User completed the trade.");
            poke.TradeFinished(this, received);

            // Only log if we completed the trade.
            UpdateCountsAndExport(poke, received, toSend);

            // Still need to wait out the trade animation.
            for (var i = 0; i < 30; i++)
                await Click(A, 0_500, token).ConfigureAwait(false);

            read = await SwitchConnection.ReadBytesMainAsync(ScreenOff, 1, token);

            while (read[0] != overworld)
            {

                await Click(B, 1000, token);
                read = await SwitchConnection.ReadBytesMainAsync(ScreenOff, 1, token);

            }
            for (var i = 0; i < 6; i++)
                await Click(B, 1000, token);
            Log("done spamming b");
            // Sometimes they offered another mon, so store that immediately upon leaving Union Room.
            lastOffered = await SwitchConnection.ReadBytesAbsoluteAsync(BoxSlot1, EncryptedSize, token).ConfigureAwait(false);

            return PokeTradeResult.Success;
        }

        private void UpdateCountsAndExport(PokeTradeDetail<PB7> poke, PB7 received, PB7 toSend)
        {
            var counts = TradeSettings;
            if (poke.Type == PokeTradeType.Random)
                counts.AddCompletedDistribution();
            else
                counts.AddCompletedTrade();

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
            {
                var subfolder = poke.Type.ToString().ToLower();
                DumpPokemon(DumpSetting.DumpFolder, subfolder, received); // received by bot
                if (poke.Type is PokeTradeType.Specific)
                    DumpPokemon(DumpSetting.DumpFolder, "traded", toSend); // sent to partner
            }
        }

        private async Task<PokeTradeResult> ConfirmAndStartTrading(PokeTradeDetail<PB7> detail, CancellationToken token)
        {
            // We'll keep watching B1S1 for a change to indicate a trade started -> should try quitting at that point.
            var oldEC = await LGReadPokemon(BoxSlot1, token);

            while (BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(ScreenOff, 2, token), 0) == Boxscreen)
            {
                await Click(A, 1000, token);
                await Task.Delay(2_000).ConfigureAwait(false);
            }

            Log("waiting on trade screen");
            await Task.Delay(20_000).ConfigureAwait(false);
            await Click(A, 200, token).ConfigureAwait(false);
            Log("trading...");
            await Task.Delay(15000);
            var tradeCounter = 0;
            while (tradeCounter < 8)
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                tradeCounter++;

                var newEC = await LGReadPokemon(BoxSlot1, token);
                
                if (newEC.EncryptionConstant != oldEC.EncryptionConstant)
                {
                    while (await LGIsInTrade(token))
                        await Click(A, 1000, token);
                    return PokeTradeResult.Success;
                }

            }

            Log("If we don't detect a B1S1 change, the trade didn't go through in that time. This happened");
            return PokeTradeResult.TrainerTooSlow;
        }

        private async Task<bool> EnterUnionRoomWithCode(PokeTradeType tradeType, int tradeCode, byte[]? read, CancellationToken token)
        {

            await Click(X, 2000, token).ConfigureAwait(false);
            Log("opening menu");
            while (BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(ScreenOff, 4, token), 0) != menuscreen)
            {
                await Click(B, 2000, token);
                await Click(X, 2000, token);
            }
            Log("selecting communicate");
            await SetStick(SwitchStick.RIGHT, 30000, 0, 0, token).ConfigureAwait(false);
            await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
            while (BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(ScreenOff, 4, token), 0) == menuscreen)
            {
                await Click(A, 1000, token);
                if (BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(ScreenOff, 2, token), 0) == savescreen || BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(ScreenOff, 2, token), 0) == savescreen2)
                {
                    read = await SwitchConnection.ReadBytesMainAsync(ScreenOff, 1, token);
                    while (read[0] != overworld)
                    {

                        await Click(B, 1000, token);
                        read = await SwitchConnection.ReadBytesMainAsync(ScreenOff, 1, token);
                    }
                    await Click(X, 2000, token).ConfigureAwait(false);
                    Log("opening menu");
                    while (BitConverter.ToUInt16(await SwitchConnection.ReadBytesMainAsync(ScreenOff, 4, token), 0) != menuscreen)
                    {
                        await Click(B, 2000, token);
                        await Click(X, 2000, token);
                    }
                    Log("selecting communicate");
                    await SetStick(SwitchStick.RIGHT, 30000, 0, 0, token).ConfigureAwait(false);
                    await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
                }
            }
            await Task.Delay(2000);
            Log("selecting faraway connection");

            await SetStick(SwitchStick.RIGHT, 0, -30000, 0, token).ConfigureAwait(false);
            await SetStick(SwitchStick.RIGHT, 0, 0, 0, token).ConfigureAwait(false);
            await Click(A, 10000, token).ConfigureAwait(false);

            await Click(A, 1000, token).ConfigureAwait(false);

            await EnterLinkCode(tradeCode, Hub.Config, token).ConfigureAwait(false);

            Log("Searching for User");
            return true;
        }


        // These don't change per session and we access them frequently, so set these each time we start.

        // todo: future
        protected virtual async Task<bool> IsUserBeingShifty(PokeTradeDetail<PB8> detail, CancellationToken token)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return false;
        }

        private async Task RestartGameLGPE(CancellationToken token)
        {
            await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
            sessionTradeCount = 0;
        }

        private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PB7> detail, CancellationToken token)
        {
            int ctr = 0;
            var time = TimeSpan.FromSeconds(Hub.Config.Trade.MaxDumpTradeTime);
            var start = DateTime.Now;

            while (ctr < Hub.Config.Trade.MaxDumpsPerTrade && DateTime.Now - start < time)
            {
                // Wait for user input... Needs to be different from the previously offered Pokémon.
                var offereddata = await SwitchConnection.ReadBytesAsync(OfferedPokemon, 0x104, token);
                var offeredpbm = new PB7(offereddata);
                var tradeOffered = await ReadUntilChanged2(offeredpbm, 3_000, 1_000, token).ConfigureAwait(false);
                if (!tradeOffered)
                    continue;

                // If we detected a change, they offered something.
                var pk = new PB7(await SwitchConnection.ReadBytesAsync(OfferedPokemon, 0x104, token));
                var newEC = await SwitchConnection.ReadBytesAsync(BoxSlot1, EncryptedSize, token).ConfigureAwait(false);
                if (pk.Species < 1 || !pk.ChecksumValid || lastOffered == newEC)
                    continue;
                lastOffered = newEC;

                // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
                if (DumpSetting.Dump)
                {
                    var subfolder = detail.Type.ToString().ToLower();
                    DumpPokemon(DumpSetting.DumpFolder, subfolder, pk); // received
                }

                var la = new LegalityAnalysis(pk);
                var verbose = la.Report(true);
                Log($"Shown Pokémon is: {(la.Valid ? "Valid" : "Invalid")}.");

                ctr++;
                var msg = Hub.Config.Trade.DumpTradeLegalityCheck ? verbose : $"File {ctr}";
                detail.SendNotification(this, pk, msg);
            }

            Log($"Ended Dump loop after processing {ctr} Pokémon.");
            if (ctr == 0)
                return PokeTradeResult.TrainerTooSlow;

            TradeSettings.AddCompletedDumps();
            detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
            detail.Notifier.TradeFinished(this, detail, detail.TradeData); // blank pb7
            return PokeTradeResult.Success;
        }

        protected virtual async Task<(PB7 toSend, PokeTradeResult check)> GetEntityToSend(SAV7b sav, PokeTradeDetail<PB7> poke, PB7 offered, PB7 toSend, PartnerDataHolder partnerID, CancellationToken token)
        {
            return poke.Type switch
            {
                PokeTradeType.Random => await HandleRandomLedy(sav, poke, offered, toSend, partnerID, token).ConfigureAwait(false),
                _ => (toSend, PokeTradeResult.Success),
            };
        }

        private async Task<(PB7 toSend, PokeTradeResult check)> HandleRandomLedy(SAV7b sav, PokeTradeDetail<PB7> poke, PB7 offered, PB7 toSend, PartnerDataHolder partner, CancellationToken token)
        {
            // Allow the trade partner to do a Ledy swap.
            var config = Hub.Config.Distribution;
            var trade = Hub.Ledy.GetLedyTrade(offered, partner.TrainerOnlineID, config.LedySpecies);
            if (trade != null)
            {
                if (trade.Type == LedyResponseType.AbuseDetected)
                {
                    var msg = $"Found {partner.TrainerName} has been detected for abusing Ledy trades.";
                    EchoUtil.Echo(msg);

                    return (toSend, PokeTradeResult.SuspiciousActivity);
                }

                toSend = trade.Receive;
                poke.TradeData = toSend;

                poke.SendNotification(this, "Injecting the requested Pokémon.");
                await Click(A, 0_800, token).ConfigureAwait(false);
                await SetBoxPokemonAbsolute(1, 0, toSend, token, sav).ConfigureAwait(false);
                await Task.Delay(2_500, token).ConfigureAwait(false);
            }
            else if (config.LedyQuitIfNoMatch)
            {
                return (toSend, PokeTradeResult.TrainerRequestBad);
            }

            for (int i = 0; i < 5; i++)
            {
                await Click(A, 0_500, token).ConfigureAwait(false);
            }

            return (toSend, PokeTradeResult.Success);
        }

        private void WaitAtBarrierIfApplicable(CancellationToken token)
        {
            if (!ShouldWaitAtBarrier)
                return;
            var opt = Hub.Config.Distribution.SynchronizeBots;
            if (opt == BotSyncOption.NoSync)
                return;

            var timeoutAfter = Hub.Config.Distribution.SynchronizeTimeout;
            if (FailedBarrier == 1) // failed last iteration
                timeoutAfter *= 2; // try to re-sync in the event things are too slow.

            var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

            if (result)
            {
                FailedBarrier = 0;
                return;
            }

            FailedBarrier++;
            Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
        }

        /// <summary>
        /// Checks if the barrier needs to get updated to consider this bot.
        /// If it should be considered, it adds it to the barrier if it is not already added.
        /// If it should not be considered, it removes it from the barrier if not already removed.
        /// </summary>
        private void UpdateBarrier(bool shouldWait)
        {
            if (ShouldWaitAtBarrier == shouldWait)
                return; // no change required

            ShouldWaitAtBarrier = shouldWait;
            if (shouldWait)
            {
                Hub.BotSync.Barrier.AddParticipant();
                Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
            else
            {
                Hub.BotSync.Barrier.RemoveParticipant();
                Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
        }

   
        private async Task<bool> SetBoxPkmWithSwappedIDDetailsLGPE(PB7 toSend, PB7 offered, SAV7b sav, TradePartnerLGPE tradePartnerino, CancellationToken token)
        {
            Log($"New requested Pokemon: {(Species)toSend.Species}, TName: {toSend.OT_Name}, TID: {toSend.TrainerTID7}, Language: {toSend.Language}, OTGender: {toSend.OT_Gender} \r\n Offered Pokemon: {(Species)offered.Species}, TName: {offered.OT_Name}, TID: {offered.TrainerTID7}, Language: {offered.Language}, OTGender: {offered.OT_Gender}");
            var cln = (PB7)toSend.Clone();
            cln.OT_Gender = offered.OT_Gender;
           cln.TrainerTID7 = offered.TrainerTID7;
           cln.TrainerSID7 = offered.TrainerSID7;
         //  cln.TID = tradePartner.TID7;
         //  cln.SID = tradePartner.SID7;
            cln.Language = offered.Language;
            cln.OT_Name = tradePartnerino.TrainerName;
            if (cln.IsEgg == false)
                cln.ClearNickname();

            if (toSend.IsShiny)
                cln.SetShiny();

            cln.RefreshChecksum();

            var tradela = new LegalityAnalysis(cln);
            if (tradela.Valid)
            {
                Log($"Pokemon is vaild, changing now \r\n New Sending Pokemon: {(Species)cln.Species}, TName: {cln.OT_Name}, TID: {cln.TrainerTID7}, Language: {cln.Language}, OTGender: {cln.OT_Gender}");
                await SetBoxPokemonAbsolute(1, 0, cln, token, sav).ConfigureAwait(false);
            }
            else
            {
                Log($"Pokemon not vaild, something went wrong. Still trying to trade Pokemon \r\n New Offered Pokemon: {(Species)cln.Species}, TName: {cln.OT_Name}, TID: {cln.TrainerTID7}, Language: {cln.Language}, OTGender: {cln.OT_Gender}");
                await SetBoxPokemonAbsolute(1, 0, cln, token, sav).ConfigureAwait(false);
            }
            return tradela.Valid;
        }

        public static Bitmap Merge(System.Drawing.Image firstImage, System.Drawing.Image secondImage, System.Drawing.Image thirdImage)
        {
            if (firstImage == null)
            {
                throw new ArgumentNullException("firstImage");
            }

            if (secondImage == null)
            {
                throw new ArgumentNullException("secondImage");
            }

            if (thirdImage == null)
            {
                throw new ArgumentNullException("thirdImage");
            }
            firstImage = ResizeImage(firstImage, 137, 130);
            secondImage = ResizeImage(secondImage, 137, 130);
            thirdImage = ResizeImage(thirdImage, 137, 130);
#pragma warning disable CA1416 // Plattformkompatibilität überprüfen
            int outputImageWidth = firstImage.Width + 20;

            int outputImageHeight = firstImage.Height - 65;


            Bitmap outputImage = new(outputImageWidth, outputImageHeight, PixelFormat.Format32bppArgb);


            using (Graphics graphics = Graphics.FromImage(outputImage))
            {
                graphics.DrawImage(firstImage, new Rectangle(0, 0, firstImage.Width, firstImage.Height),
                    new Rectangle(new Point(), firstImage.Size), GraphicsUnit.Pixel);
                graphics.DrawImage(secondImage, new Rectangle(50, 0, secondImage.Width, secondImage.Height),
                    new Rectangle(new Point(), secondImage.Size), GraphicsUnit.Pixel);
                graphics.DrawImage(thirdImage, new Rectangle(100, 0, thirdImage.Width, thirdImage.Height),
                    new Rectangle(new Point(), thirdImage.Size), GraphicsUnit.Pixel);
#pragma warning restore CA1416 // Plattformkompatibilität überprüfen
            }

            return outputImage;
        }

        public static Bitmap ResizeImage(Image image, int width, int height)
        {
            var destRect = new Rectangle(-40, -65, width, height);
            var destImage = new Bitmap(width, height);

            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                }
            }
            return destImage;
        }

        public async Task<bool> ReadUntilChanged2(PB7 lastOffered, int waitms, int waitInterval, CancellationToken token)
        {
            var sw = new Stopwatch();
            sw.Start();
            do
            {
                PB7? offered = new PB7(await SwitchConnection.ReadBytesAsync(OfferedPokemon, 0x104, token).ConfigureAwait(false));
                if (offered.EncryptionConstant != lastOffered.EncryptionConstant)

                    return true;

                await Task.Delay(waitInterval, token).ConfigureAwait(false);
            } while (sw.ElapsedMilliseconds < waitms);
            return false;
        }

    }
}