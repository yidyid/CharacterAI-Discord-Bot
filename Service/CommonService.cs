﻿using System.Text.RegularExpressions;
using Discord.WebSocket;
using Discord;
using CharacterAI_Discord_Bot.Models;
using CharacterAI_Discord_Bot.Handlers;
using Microsoft.Extensions.DependencyInjection;
using static CharacterAI_Discord_Bot.Service.CommandsService;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;
using System.Threading.Channels;
using System;

namespace CharacterAI_Discord_Bot.Service
{
    public partial class CommonService
    {
        internal static Config BotConfig { get => _config; }
        private static readonly Config _config = GetConfig()!;

        private static readonly string _imgPath = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "img" + Path.DirectorySeparatorChar;
        private static readonly string _storagePath = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "storage" + Path.DirectorySeparatorChar;
        private static readonly HttpClient _httpClient = new();

        internal static readonly string nopowerPath = _imgPath + _config.Nopower;
        internal static readonly string defaultAvatarPath = _imgPath + "defaultAvatar.png";

        public static async Task AutoSetup(ServiceProvider services, DiscordSocketClient client)
        {
            var handler = services.GetRequiredService<CommandsHandler>();
            var cI = handler.CurrentIntegration;
            var result = await cI.SetupAsync(_config.AutoCharId);
            if (!result.IsSuccessful) return;

            var savedData = GetStoredData(_config.AutoCharId);

            handler.BlackList = savedData.BlackList;
            Log("Restored blocked users: ");
            Success(handler.BlackList.Count.ToString());

            handler.Channels = savedData.Channels;
            Log("Restored channels: ");
            Success(handler.Channels.Count.ToString());

            // GetYAML will return empty channels list if character was changed
            // This call will delete all records with previous character
            SaveData(channels: handler.Channels);

            if (BotConfig.DescriptionInPlaying)
                await UpdatePlayingStatus(client, type: 0, integration: cI).ConfigureAwait(false);
            if (BotConfig.CharacterAvatarEnabled)
                await SetBotAvatar(client.CurrentUser, cI.CurrentCharacter).ConfigureAwait(false);
            if (BotConfig.CharacterNameEnabled)
                await SetBotNickname(cI.CurrentCharacter.Name!, client).ConfigureAwait(false);
        }

        internal static void SaveData(List<Models.Channel>? channels = null, List<ulong>? blackList = null)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();

            if (blackList is not null && blackList.Any())
            {
                string blackListYAML = string.Join(", ", blackList);
                File.WriteAllText(_storagePath + "blacklist.yaml", blackListYAML);
            }

            if (channels is null) return;

            string channelsYAML = "";
            foreach (var instance in channels)
            {
                var line = serializer.Serialize(new
                {
                    instance.Id,
                    instance.AuthorId,
                    instance.Data.HistoryId,
                    instance.Data.CharacterId,
                    instance.GuestsList
                });
                channelsYAML += line += "-------------------\n";
            }
            File.WriteAllText(_storagePath + "channels.yaml", channelsYAML);
        }

        internal static dynamic GetStoredData(string currectCharId)
        {
            if (!File.Exists(_storagePath + "blacklist.yaml"))
                File.Create(_storagePath + "blacklist.yaml");
            if (!File.Exists(_storagePath + "channels.yaml"))
                File.Create(_storagePath + "channels.yaml");

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();   

            var blackListYAML = File.ReadAllText(_storagePath + "blacklist.yaml");
            var channelsYAML = File.ReadAllText(_storagePath + "channels.yaml");

            var blackList = new List<ulong>();
            if (!string.IsNullOrEmpty(blackListYAML))
                foreach (var id in blackListYAML.Split(", "))
                    blackList.Add(ulong.Parse(id));

            var channels = new List<Models.Channel>();
            
            foreach (string line in channelsYAML.Split("-------------------"))
            {
                if (line.Length < 5) continue;

                var channelTemp = deserializer.Deserialize<ChannelTemp>(line);
                string characterId = channelTemp.CharacterId!;

                if (characterId != currectCharId)
                    return new { BlackList = blackList, Channels = channels };

                ulong channelId = channelTemp.Id;
                ulong authorId = channelTemp.AuthorId;
                string historyId = channelTemp.HistoryId!;
                var channel = new Models.Channel(channelId, authorId, historyId, characterId);
                channel.GuestsList = channelTemp.GuestsList!;

                channels.Add(channel);
            }

            return new { BlackList = blackList, Channels = channels };
        }

        public static bool HelloLog(CharacterAI.Models.Character charInfo)
        {
            Log("\nCharacterAI - Connected\n\n", ConsoleColor.Green);
            Log($" [{charInfo.Name}]\n\n", ConsoleColor.Cyan);
            Log($"{charInfo.Greeting}\n");
            if (!string.IsNullOrEmpty(charInfo.Description))
                Log($"\"{charInfo.Description}\"\n");
            Log("\nSetup complete\n", ConsoleColor.Yellow);

            return Success(new string('<', 50) + "\n");
        }
        public static Embed BuildCharactersList(LastSearchQuery args)
        {
            var list = new EmbedBuilder()
                .WithTitle($"Characters found by query \"{args.Query}\":\n({args.Response!.Characters!.Count})\n")
                .WithFooter($"Page {args.CurrentPage}/{args.Pages}");

            // Fill with first 10 or less
            int tail = args.Response.Characters.Count - (args.CurrentPage - 1) * 10;
            int rows = tail > 10 ? 10 : tail;

            for (int i = 0; i < rows; i++)
            {
                int index = (args.CurrentPage - 1) * 10 + i;
                var character = args.Response.Characters[index];
                string fTitle = character.Name!;

                if (i + 1 == args.CurrentRow)
                    fTitle += " - ✅";

                list.AddField($"{index + 1}. {fTitle}", $"Interactions: {character.Interactions} | Author: {character.Author}");
            }

            return list.Build();
        }

        public static string RemoveMention(string text)
        {
            text = text.Trim();
            // Remove first mention
            if (text.StartsWith("<"))
                text = new Regex("\\<(.*?)\\>").Replace(text, "", 1);
            // Remove prefix
            foreach (string prefix in _config.BotPrefixes)
                if (text.StartsWith(prefix))
                    text = text.Replace(prefix, "");

            return text;
        }

        public static async Task<byte[]?> TryDownloadImg(string url, int attempts)
        {
            if (string.IsNullOrEmpty(url)) return null;

            using HttpClient client = new();
            // Try n times and return null
            for (int i = 0; i < attempts; i++)
            {
                try { return await client.GetByteArrayAsync(url).ConfigureAwait(false); }
                catch { await Task.Delay(2500); }
            }

            return null;
        }

        // Simply checks if image is avalable.
        // (cAI is used to have broken undownloadable images or sometimes it's just
        //  takes eternity for it to upload one on server, but image url is provided in advance)
        public static async Task<bool> TryGetImage(string url)
        {
            for (int i = 0; i < 10; i++)
                if ((await _httpClient.GetAsync(url).ConfigureAwait(false)).IsSuccessStatusCode)
                    return true;
                else
                    await Task.Delay(3000);

            return false;
        }

        public static string AddQuote(string text, SocketUserMessage message)
        {
            var refMsg = message.ReferencedMessage;
            if (refMsg is not null && !string.IsNullOrEmpty(refMsg.Content))
                text = $"((In response to: \"{RemoveMention(refMsg.Content)}\"))\n{text}";

            return text;
        }

        public static string AddUsername(string text, SocketUserMessage message)
        {
            string author = message.Author.Username;
            if (!string.IsNullOrEmpty(text) && !string.IsNullOrWhiteSpace(text))
                text = $"User [{author}] says:\n{text}";

            return text;
        }

        // Log and return true
        public static bool Success(string logText = "")
        {
            Log(logText + "\n", ConsoleColor.Green);

            return true;
        }

        // Log and return false
        public static bool Failure(string logText = "", HttpResponseMessage? response = null)
        {
            if (logText != "")
                Log(logText + "\n", ConsoleColor.Red);

            if (response is not null)
            {
                var request = response.RequestMessage!;
                var url = request.RequestUri;
                var responseContent = response.Content?.ReadAsStringAsync().Result;
                var requestContent = request.Content?.ReadAsStringAsync().Result;

                Log($"Error!\n Request failed! ({url})\n", ConsoleColor.Red);
                Log(color: ConsoleColor.Red,
                    text: $" Response: {response.ReasonPhrase}\n" +
                          (requestContent is null ? "" : $" Request Content: {requestContent}\n") +
                          (requestContent is null ? "" : $" Response Content: {responseContent}\n")
                    );
            }

            return false;
        }

        public static void Log(string text, ConsoleColor color = ConsoleColor.Gray)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ResetColor();
        }

        public static dynamic? GetConfig()
        {
            var path = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + "Config.json";
            using StreamReader configJson = new(path);
            try
            {
                return new Config(configJson);
            }
            catch
            {
                Failure("Something went wrong... Check your Config file.\n");
                return null;
            }
        }


        // probably not useless
        //public static async Task CreateRole(DiscordSocketClient client)
        //{
        //    var guild = client.Guilds.FirstOrDefault();
        //    var role = client.GetGuild(guild.Id).Roles.FirstOrDefault(role => role.Name == CommonService.GetConfig().botRole);
        //    if (!string.IsNullOrEmpty(role.ToString)) return;

        //    try
        //    {
        //        Log("Creating role... ");
        //        var newRole = await guild.CreateRoleAsync(GetConfig().botRole).Result;
        //        await guild.Owner.AddRoleAsync(newRole);
        //    }
        //    catch { Failure("Failed to create default bot role. Probably, missing permissions?"); }

        //    Success("OK\n");
        //}
    }

    internal class ChannelTemp
    {
        public ulong Id { get; set; }
        public ulong AuthorId { get; set; }
        public string? HistoryId { get; set; }
        public string? CharacterId { get; set; }
        public List<ulong>? GuestsList { get; set; }
    };

}
