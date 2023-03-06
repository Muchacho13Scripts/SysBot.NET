using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.IO;
using PKHeX.Core;
using SysBot.Base;

namespace SysBot.Pokemon.Twitch
{
    public static class TwitchCommandsHelper<T> where T : PKM, new()
    {

        // Helper functions for commands
        public static bool AddToWaitingList(string setstring, string display, string username, ulong mUserId, bool sub, bool multi, PokeTradeHub<T> Hub, out string msg)
        {
            if (!TwitchBot<T>.Info.GetCanQueue())
            {
                msg = "Sorry, I am not currently accepting queue requests! Check announcements and the Discord Server for more infos!";
                return false;
            }



            if (setstring == "" || setstring == null)
            {
                msg = $"@{username} rocket346Sad Cancelled rocket346Sad No Pokemon selected.";
                return false;
            }

            if (multi)
            {

                if (!sub)
                {
                    msg = $"@{username} rocket346Sad Cancelled rocket346Sad Become Subscriber to enable Multi-Trades. Type '!guide' or join our Discord for help: https://discord.gg/W4y2Uc9vZV";
                    return false;
                }

                CreateMultiFolder(display);

                if (setstring.ToLower() == "reset" || setstring.ToLower() == "clear")
                {
                    if (Directory.Exists(Path.Combine("multitrade", display)))
                    {
                        System.IO.DirectoryInfo di = new(Path.Combine("multitrade", display));
                        var multicount = di.GetFiles().Length;
                        if (multicount == 0)
                        {
                            msg = $"@{username} - Your {GameAbbreviation()} Multilist is already empty";
                            return false;
                        }

                        foreach (FileInfo file in di.GetFiles())
                        {
                            file.Delete();
                        }
                        msg = $"@{username} - I released all Pokemon in your {GameAbbreviation()} Multilist. Bye-Bye!";
                        return false;
                    }
                    else
                    {
                        msg = $"@{username} - Your {GameAbbreviation()} Multilist is already empty";
                        return false;
                    }

                }
            } //handling Multi-Trades preparations like folder creation, Reset and Non-Subs

            try
            {
                PKM? pkm = TryFetchFromDistributeDirectory(setstring.Trim());
                string result = string.Empty;
                var reason = string.Empty;

                // check if egg was requested but wasnt found it in distribution folder
                if (pkm == null && (setstring.ToLower().Contains("(egg)") || setstring.ToLower()[..4] == "egg "))
                {
                    msg = $"@{username} rocket346Sad Cancelled rocket346Sad Can't find requested Egg or incorrect trade request. Type '!guide' or join our Discord for help: https://discord.gg/W4y2Uc9vZV";
                    return false;
                }

                if (pkm == null)
                {
                    var set = ShowdownUtil.ConvertToShowdown(setstring);
                    if (set == null)
                    {
                        msg = $"Skipping trade, @{username}: Empty nickname provided for the species.";
                        return false;
                    }
                    var template = AutoLegalityWrapper.GetTemplate(set);
                    if (template.Species < 1)
                    {
                        msg = $"@{username} rocket346Sad Cancelled rocket346Sad Incorrect Trade request. Type '!guide' or join our Discord for help: https://discord.gg/W4y2Uc9vZV";
                        return false;
                    }

                    if (set.InvalidLines.Count != 0)
                    {
                        msg = $"Skipping trade, @{username}: Unable to parse Showdown Set:\n{string.Join("\n", set.InvalidLines)}";
                        return false;
                    }

                    var sav = AutoLegalityWrapper.GetTrainerInfo<T>();
                    pkm = sav.GetLegal(template, out result);
                    reason = result == "Timeout" ? "Set took too long to generate." : "illegal Pokémon.";
                    if (result == "Failed")
                        reason += $" Hint: {AutoLegalityWrapper.GetLegalizationHint(template, sav, pkm)} Type '!tradeguide' or join our Discord for help: https://discord.gg/W4y2Uc9vZV";


                }

                if (!pkm.CanBeTraded())
                {
                    msg = $"Skipping trade, @{username}: Provided Pokémon is probably fusioned and blocked from trading!";
                    return false;
                }

                if (pkm is T pk)
                {
                    var valid = new LegalityAnalysis(pkm).Valid;

                    //Check Subs: adds Auto-OT for Subs and removes Custom OT for Non-Subs
                    SubChecks(sub, pkm, Hub);

                    if (multi && valid)
                    {
                        int fCount = Directory.GetFiles((Path.Combine("multitrade", display)), "*", SearchOption.TopDirectoryOnly).Length;
                        if (fCount >= Hub.Config.Trade.MaxMultiPokemon)
                        {
                            msg = $"@{username} - rocket346Sad Cancelled rocket346Sad You already have {Hub.Config.Trade.MaxMultiPokemon} Pokemon waiting for you! You can now whisper a trade code of your choice to me to enter the queue!";
                        }
                        else
                        {
                            PokeRoutineExecutor<T>.DumpPokemon("multitrade", display, pk);
                            msg = $"@{username} rocket346{GameAbbreviation()} NICE rocket346{GameAbbreviation()} Added {(Species)pkm.Species} to your {GameAbbreviation()} Multi List. {fCount + 1} Pokemon in total.";
                            msg += fCount == Hub.Config.Trade.MaxMultiPokemon - 1 ? "Maximum number of Pokemon reached. You can now whisper a trade code of your choice to me!" : " You can now whisper a trade code of your choice to me to enter the queue OR add another Pokemon!";
                        }
                        pkm.OT_Name = "multitrade";
                        pkm.TrainerSID7 = 1338;
                        var tqq = new TwitchQueue<T>(pk, new PokeTradeTrainerInfo(display, mUserId), username, sub);
                        TwitchBot<T>.QueuePool.RemoveAll(z => z.UserName == username); // remove old requests if any
                        TwitchBot<T>.QueuePool.Add(tqq);
                        return true;

                    }

                    if (valid)
                    {
                        var tq = new TwitchQueue<T>(pk, new PokeTradeTrainerInfo(display, mUserId), username, sub);
                        TwitchBot<T>.QueuePool.RemoveAll(z => z.UserName == username); // remove old requests if any
                        TwitchBot<T>.QueuePool.Add(tq);
                        msg = $"@{username} rocket346{GameAbbreviation()} NICE rocket346{GameAbbreviation()} {(Species)pkm.Species} created for {GameAbbreviation()}. Whisper a trade code of your choice to ME, not the STREAMER! Click my Name and whisper OR type in chat: /w {Hub.Config.Twitch.Username} [8 digitcode]";
                        return true;
                    }
                }

                msg = $"@{username} rocket346Sad Cancelled rocket346Sad {reason}";
            }
            catch (Exception ex)
            {
                LogUtil.LogSafe(ex, nameof(TwitchCommandsHelper<T>));
                msg = $"@{username}: Cancelled, an unexpected error occurred. Maybe the Pokemon is not yet available in the game.";
            }
            return false;
        }

        public static string ClearTrade(string user)
        {
            var result = TwitchBot<T>.Info.ClearTrade(user);
            return GetClearTradeMessage(result);
        }

        public static string ClearTrade(ulong userID)
        {
            var result = TwitchBot<T>.Info.ClearTrade(userID);
            return GetClearTradeMessage(result);
        }

        private static string GetClearTradeMessage(QueueResultRemove result)
        {
            return result switch
            {
                QueueResultRemove.CurrentlyProcessing => $"Looks like you're currently being processed! Did not remove from {GameAbbreviation()} queue.",
                QueueResultRemove.CurrentlyProcessingRemoved => $"Looks like you're currently being processed! Removed from {GameAbbreviation()} queue.",
                QueueResultRemove.Removed => $"Removed you from the {GameAbbreviation()} queue.",
                _ => "",
            };
        }

        public static string GetCode(ulong parse)
        {
            var detail = TwitchBot<T>.Info.GetDetail(parse);
            return detail == null
                ? "Sorry, you are not currently in the queue."
                : $"Your trade code is {detail.Trade.Code:0000 0000}";
        }

        //custom stuff
        private static PKM? SubChecks(bool sub, PKM pkm, PokeTradeHub<T> Hub)
        {
            var temp = pkm.OT_Name;

            if (sub && pkm.OT_Name == Hub.Config.Legality.GenerateOT)
                pkm.OT_Name = "AutoOT";

            else if (!sub)
                if ((pkm.Language == 1 || pkm.Language == 9 || pkm.Language == 7 || pkm.Language == 8) && Hub.Config.Legality.GenerateOT.Length > 6)
                    pkm.OT_Name = Hub.Config.Legality.GenerateOT[0..6];
                else
                    pkm.OT_Name = Hub.Config.Legality.GenerateOT;

            var otcheck = new LegalityAnalysis(pkm).Valid; // check if a custom OT is still legal
            if (!otcheck)
            {
                pkm.OT_Name = temp; //if not, OT change gets undone
            }

            return pkm;
        }

        private static void CreateMultiFolder(string display)
        {
            if (!Directory.Exists($"multitrade"))
            {
                Directory.CreateDirectory($"multitrade");
            }

            if (!Directory.Exists(Path.Combine("multitrade", display)))
            {
                Directory.CreateDirectory(Path.Combine("multitrade", display));
            }
        }

        private static string GameAbbreviation()
        {
            if (typeof(T) == typeof(PK8))
                return "SWSH";
            else if (typeof(T) == typeof(PB8))
                return "BDSP";
            else if (typeof(T) == typeof(PA8))
                return "PLA";
            else if (typeof(T) == typeof(PK9))
                return "SV";
            else if (typeof(T) == typeof(PB7))
                return "LGPE";
            else
                return "";
        }

        //thx beri
        public static T? TryFetchFromDistributeDirectory(string set)
        {
            char[] MyChar = { ':', '#', '%', '&', '{', '}', '\\', '$', '!', '@' };
            set = Filter(set, MyChar).ToLower();
            try
            {
                var folder = TwitchBot<T>.Info.Hub.Config.Folder.DistributeFolder;
                if (!Directory.Exists(folder))
                    return null;

                var path = Path.Combine(folder, set);
                if (!File.Exists(path))
                    path += ".pk9";
                if (!File.Exists(path))
                    return null;

                var data = File.ReadAllBytes(path);
                var prefer = EntityFileExtension.GetContextFromExtension(path, EntityContext.None);
                var pkm = EntityFormat.GetFromBytes(data, prefer);
                if (pkm is null)
                    return null;
                if (pkm is not T)
                    pkm = EntityConverter.ConvertToType(pkm, typeof(T), out _);
                if (pkm is not T dest)
                    return null;

                if (!dest.CanBeTraded())
                    return null;

                // Legality analysis happens outside of this function
                return dest;
            }
            catch (Exception e) { LogUtil.LogSafe(e, nameof(TwitchCommandsHelper<T>)); }

            return null;
        }

        public static string Filter(string str, char[] charsToRemove)
        {
            foreach (char c in charsToRemove)
            {
                str = str.Replace(c.ToString(), String.Empty);
            }

            return str;
        }
    }
}
