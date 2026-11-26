using static PzServerUi.Application.ServerIniGroup;
using static PzServerUi.Application.ServerIniKind;

namespace PzServerUi.Application;

internal sealed record ServerIniDefinition(ServerIniGroup Group, ServerIniKind Kind,
    string? Label = null, int Min = int.MinValue, int Max = int.MaxValue);

internal static class ServerIniCatalog
{
    private static readonly Dictionary<string, ServerIniDefinition> Definitions = Create();
    private static readonly Dictionary<string, string> CanonicalKeys =
        Definitions.Keys.ToDictionary(key => key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Fallbacks { get; } = new Dictionary<string, string>
    {
        ["MaxPlayers"] = "32", ["PVP"] = "true",
        ["Mods"] = "", ["WorkshopItems"] = "",
        ["DefaultPort"] = FormattableString.Invariant($"{ServerGamePorts.StandardDefaultPort}"),
        ["UDPPort"] = FormattableString.Invariant($"{ServerGamePorts.StandardUdpPort}")
    };

    public static string CanonicalKey(string key) => CanonicalKeys.GetValueOrDefault(key, key);

    public static ServerIniDefinition Describe(string key) => Definitions.GetValueOrDefault(key)
        ?? new(Other, key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase) ? Secret : Text);

    private static Dictionary<string, ServerIniDefinition> Create()
    {
        // Build 42 option inventory: https://pzwiki.net/wiki/Server_settings
        // The profile remains the authority: do not synthesize version-specific keys.
        var map = new Dictionary<string, ServerIniDefinition>(StringComparer.Ordinal);
        Add(General, Text, "PublicName PublicDescription ServerWelcomeMessage");
        Add(General, Flag, "Public PauseEmpty DisableScoreboard HideAdminsInPlayerList ShowCoordinates");
        Add(Access, Flag, "Open AutoCreateUserInWhiteList DropOffWhiteListAfterDeath AllowCoop AllowNonAsciiUsername SteamVAC DoLuaChecksum");
        Add(Access, Integer, "MaxAccountsPerUser");
        Add(Access, Secret, "Password");
        Add(Network, Text, "server_browser_announced_ip");
        Add(Network, Secret, "RCONPassword");
        Add(Network, Flag, "UPnP DenyLoginOnOverloadedServer LoginQueueEnabled");
        Add(Network, Integer, "PingLimit LoginQueueConnectTimeout MaxPacketsPerSecond");
        Add(Gameplay, Text, "SpawnPoint SpawnItems");
        Add(Gameplay, Flag, "PVP SafetySystem ShowSafety DisplayUserName ShowFirstAndLastName UsernameDisguises HideDisguisedUserName SleepAllowed SleepNeeded KnockedDownAllowed SneakModeHideFromOtherPlayers UltraSpeedDoesnotAffectToAnimals PlayerRespawnWithSelf PlayerRespawnWithOther RemovePlayerCorpsesOnCorpseRemoval TrashDeleteAll PVPMeleeWhileHitReaction MouseOverToSeeDisplayName HidePlayersBehindYou PlayerBumpPlayer DisableVehicleTowing DisableTrailerTowing DisableBurntTowing SwitchZombiesOwnershipEachUpdate UsePhysicsHitReaction");
        Add(Gameplay, Integer, "SafetyToggleTimer SafetyCooldownTimer SafetyDisconnectDelay ItemNumbersLimitPerContainer BloodSplatLifespanDays MapRemotePlayerVisibility HoursForLootRespawn MaxItemsForLootRespawn HoursForZombiesRespawn IncreaseZombieRespawnBy");
        Add(Gameplay, Real, "SpeedLimit FastForwardMultiplier PVPMeleeDamageModifier PVPFirearmDamageModifier CarEngineAttractionModifier");
        Add(Safehouses, Flag, "SafehousePreventsLootRespawn PlayerSafehouse AdminSafehouse SafehouseAllowTrepass SafehouseAllowFire SafehouseAllowLoot SafehouseAllowRespawn SafehouseAllowNonResidential SafehouseDisableDisguises AllowDestructionBySledgehammer SledgehammerOnlyInSafehouse DisableSafehouseWhenOwnerConnected War Faction");
        Add(Safehouses, Integer, "SafehouseDaySurvivedToClaim SafeHouseRemovalTime MaxSafezoneSize WarStartDelay WarDuration WarSafehouseHitPoints FactionDaySurvivedToCreate FactionPlayersRequiredForTag");
        Add(Chat, Text, "ChatStreams DiscordChannel DiscordChannelID DiscordChatChannel DiscordLogChannel DiscordCommandChannel BadWordListFile GoodWordListFile BadWordReplacement");
        Add(Chat, Secret, "DiscordToken WebhookAddress");
        Add(Chat, Flag, "GlobalChat PVPLogToolChat DiscordEnable VoiceEnable Voice3D DisableRadioStaff DisableRadioAdmin DisableRadioGM DisableRadioOverseer DisableRadioModerator DisableRadioInvisible BanKickGlobalSound");
        Add(Chat, Integer, "BadWordPolicy ChatMessageCharacterLimit ChatMessageSlowModeTime");
        Add(Chat, Real, "VoiceMinDistance VoiceMaxDistance");
        Add(Mods, Text, "Mods WorkshopItems Map Seed");
        Add(Maintenance, Text, "ServerPlayerID ClientCommandFilter ClientActionLogs SteamScoreboard");
        Add(Maintenance, Flag, "PVPLogToolFile PerkLogs BackupsOnStart BackupsOnVersionChange");
        Add(Maintenance, Integer, "ResetID SaveWorldEveryMinutes BackupsCount BackupsPeriod MultiplayerStatisticsPeriod");
        Add(Access, Integer, "AntiCheatSafety AntiCheatSpeed AntiCheatNoClip AntiCheatHit AntiCheatPacketException AntiCheatPermission AntiCheatXP AntiCheatSafeHouse AntiCheatPlayer AntiCheatChecksum");
        map["PublicName"] = new(General, Text, "Отображаемое имя");
        map["PublicDescription"] = new(General, Text, "Описание сервера");
        map["MaxPlayers"] = new(General, Integer, "Лимит игроков", 1, 100);
        map["PVP"] = new(Gameplay, Flag, "Разрешить PvP");
        map["DefaultPort"] = new(Network, Integer, "Основной UDP-порт", 1, 65535);
        map["UDPPort"] = new(Network, Integer, "Дополнительный UDP-порт", 1, 65535);
        map["RCONPort"] = new(Network, Integer, "Порт RCON", 0, 65535);
        return map;

        void Add(ServerIniGroup group, ServerIniKind kind, string keys)
        {
            var definition = new ServerIniDefinition(group, kind);
            foreach (var key in keys.Split(' '))
                map.Add(key, definition);
        }
    }
}
