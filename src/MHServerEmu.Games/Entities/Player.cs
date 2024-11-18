﻿using System.Text;
using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Core.System.Time;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Achievements;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Dialog;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Options;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.LiveTuning;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Missions;
using MHServerEmu.Games.Navi;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.Regions.Maps;
using MHServerEmu.Games.Regions.MatchQueues;
using MHServerEmu.Games.Social.Communities;
using MHServerEmu.Games.Social.Guilds;

namespace MHServerEmu.Games.Entities
{
    // Avatar index for console versions that have local coop, mostly meaningless on PC.
    public enum PlayerAvatarIndex       
    {
        Primary,
        Secondary,
        Count
    }

    // NOTE: These badges and their descriptions are taken from an internal build dated June 2015 (most likely version 1.35).
    // They are not fully implemented and they may be outdated for our version 1.52.
    public enum AvailableBadges
    {
        CanGrantBadges = 1,         // User can grant badges to other users
        SiteCommands,               // User can run the site commands (player/regions lists, change to specific region etc)
        CanBroadcastChat,           // User can send a chat message to all players
        AllContentAccess,           // User has access to all content in the game
        CanLogInAsAnotherAccount,   // User has ability to log in as another account
        CanDisablePersistence,      // User has ability to play without saving
        PlaytestCommands,           // User can always use commands that are normally only available during a playtest (e.g. bug)
        CsrUser,                    // User can perform Customer Service Representative commands
        DangerousCheatAccess,       // User has access to some especially dangerous cheats
        NumberOfBadges
    }

    public class Player : Entity, IMissionManagerOwner
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly EventPointer<SwitchAvatarEvent> _switchAvatarEvent = new();
        private readonly EventPointer<ScheduledHUDTutorialResetEvent> _hudTutorialResetEvent = new();

        private MissionManager _missionManager = new();
        private ReplicatedPropertyCollection _avatarProperties = new();
        private ulong _shardId;
        private RepString _playerName = new();
        private ulong[] _consoleAccountIds = new ulong[(int)PlayerAvatarIndex.Count];
        private RepString _secondaryPlayerName = new();
        private MatchQueueStatus _matchQueueStatus = new();

        // NOTE: EmailVerified and AccountCreationTimestamp are set in NetMessageGiftingRestrictionsUpdate that
        // should be sent in the packet right after logging in. NetMessageGetCurrencyBalanceResponse should be
        // sent along with it.
        private bool _emailVerified;
        private TimeSpan _accountCreationTimestamp;     // UnixTime

        private RepULong _partyId = new();

        private ulong _guildId;
        private string _guildName;
        private GuildMembership _guildMembership;

        private Community _community;
        private List<PrototypeId> _unlockedInventoryList = new();
        private SortedSet<AvailableBadges> _badges = new();
        private HashSet<ulong> _tagEntities = new();
        private Queue<PrototypeId> _kismetSeqQueue = new();
        private GameplayOptions _gameplayOptions = new();
        private AchievementState _achievementState = new();
        private Dictionary<PrototypeId, StashTabOptions> _stashTabOptionsDict = new();

        // TODO: Serialize on migration
        private Dictionary<ulong, MapDiscoveryData> _mapDiscoveryDict = new();

        private TeleportData _teleportData;

        // Accessors
        public MissionManager MissionManager { get => _missionManager; }
        public ulong ShardId { get => _shardId; }
        public MatchQueueStatus MatchQueueStatus { get => _matchQueueStatus; }
        public bool EmailVerified { get => _emailVerified; set => _emailVerified = value; }
        public TimeSpan AccountCreationTimestamp { get => _accountCreationTimestamp; set => _accountCreationTimestamp = value; }
        public override ulong PartyId { get => _partyId.Get(); }
        public Community Community { get => _community; }
        public GameplayOptions GameplayOptions { get => _gameplayOptions; }
        public AchievementState AchievementState { get => _achievementState; }

        public bool IsFullscreenMoviePlaying { get => Properties[PropertyEnum.FullScreenMoviePlaying]; }
        public bool IsOnLoadingScreen { get; private set; }
        public bool IsFullscreenObscured { get => IsFullscreenMoviePlaying || IsOnLoadingScreen; }

        // Network
        public PlayerConnection PlayerConnection { get; private set; }
        public AreaOfInterest AOI { get => PlayerConnection.AOI; }

        // Avatars
        public Avatar CurrentAvatar { get; private set; }
        public HUDTutorialPrototype CurrentHUDTutorial { get; private set; }

        // Console stuff - not implemented
        public bool IsConsolePlayer { get => false; }
        public bool IsConsoleUI { get => false; }
        public bool IsUsingUnifiedStash { get => IsConsolePlayer || IsConsoleUI; }
        public bool IsInParty { get; internal set; }
        public static bool IsPlayerTradeEnabled { get; internal set; }
        public Avatar PrimaryAvatar { get => CurrentAvatar; } // Fix for PC
        public Avatar SecondaryAvatar { get; private set; }
        public int CurrentAvatarCharacterLevel { get => PrimaryAvatar?.CharacterLevel ?? 0; }
        public GuildMembership GuildMembership { get; internal set; }
        public PrototypeId ActiveChapter { get => Properties[PropertyEnum.ActiveMissionChapter]; }
        public PrototypeId Faction { get => Properties[PropertyEnum.Faction]; }
        public ulong DialogTargetId { get; private set; }
        public ulong DialogInteractorId { get; private set; }

        public Player(Game game) : base(game)
        {
            _missionManager.Owner = this;
            _gameplayOptions.SetOwner(this);
        }

        public override bool Initialize(EntitySettings settings)
        {
            base.Initialize(settings);

            PlayerConnection = settings.PlayerConnection;
            _playerName.Set(settings.PlayerName);

            _shardId = 3;   // value from packet dumps

            Game.EntityManager.AddPlayer(this);
            _matchQueueStatus.SetOwner(this);

            _community = new(this);
            _community.Initialize();

            // Default loading screen before we start loading into a region
            QueueLoadingScreen(PrototypeId.Invalid);

            return true;
        }

        public override void OnPostInit(EntitySettings settings)
        {
            base.OnPostInit(settings);

            // TODO: Clean this up
            //---

            foreach (PrototypeId avatarRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                if (avatarRef == (PrototypeId)6044485448390219466) continue;   //zzzBrevikOLD.prototype
                Properties[PropertyEnum.AvatarUnlock, avatarRef] = (int)AvatarUnlockType.Type2;
            }

            foreach (PrototypeId waypointRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<WaypointPrototype>(PrototypeIterateFlags.NoAbstract))
                Properties[PropertyEnum.Waypoint, waypointRef] = true;

            foreach (PrototypeId vendorRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<VendorTypePrototype>(PrototypeIterateFlags.NoAbstract))
                Properties[PropertyEnum.VendorLevel, vendorRef] = 1;

            foreach (PrototypeId uiSystemLockRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<UISystemLockPrototype>(PrototypeIterateFlags.NoAbstract))
                Properties[PropertyEnum.UISystemLock, uiSystemLockRef] = true;

            // TODO: Set this after creating all avatar entities via a NetMessageSetProperty in the same packet
            //Properties[PropertyEnum.PlayerMaxAvatarLevel] = 60;

            // Complete all missions
            foreach (PrototypeId missionRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<MissionPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                var missionPrototype = GameDatabase.GetPrototype<MissionPrototype>(missionRef);
                if (_missionManager.ShouldCreateMission(missionPrototype))
                {
                    Mission mission = _missionManager.CreateMission(missionRef);
                    mission.SetState(MissionState.Completed);
                    mission.AddParticipant(this);
                    _missionManager.InsertMission(mission);
                }
            }

            // Todo: send this separately in NetMessageGiftingRestrictionsUpdate on login
            Properties[PropertyEnum.LoginCount] = 1075;
            _emailVerified = true;
            _accountCreationTimestamp = Clock.DateTimeToUnixTime(new(2023, 07, 16, 1, 48, 0));   // First GitHub commit date

            #region Hardcoded social tab easter eggs
            _community.AddMember(1, "DavidBrevik", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(1).SetIsOnline(1)
                .SetCurrentRegionRefId(12735255224807267622).SetCurrentDifficultyRefId((ulong)DifficultyTierPrototypeId.Normal)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(15769648016960461069).SetCostumeRefId(4881398219179434365).SetLevel(60).SetPrestigeLevel(6))
                .Build());

            _community.AddMember(2, "TonyStark", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(2).SetIsOnline(1)
                .SetCurrentRegionRefId((ulong)RegionPrototypeId.NPEAvengersTowerHUBRegion).SetCurrentDifficultyRefId((ulong)DifficultyTierPrototypeId.Normal)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(421791326977791218).SetCostumeRefId(7150542631074405762).SetLevel(60).SetPrestigeLevel(5))
                .Build());

            _community.AddMember(3, "Doomsaw", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(3).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(17750839636937086083).SetCostumeRefId(14098108758769669917).SetLevel(60).SetPrestigeLevel(6))
                .Build());

            _community.AddMember(4, "PizzaTime", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(4).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(9378552423541970369).SetCostumeRefId(6454902525769881598).SetLevel(60).SetPrestigeLevel(5))
                .Build());

            _community.AddMember(5, "RogueServerEnjoyer", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(5).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(1660250039076459846).SetCostumeRefId(9447440487974639491).SetLevel(60).SetPrestigeLevel(3))
                .Build());

            _community.AddMember(6, "WhiteQueenXOXO", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(6).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(412966192105395660).SetCostumeRefId(12724924652099869123).SetLevel(60).SetPrestigeLevel(4))
                .Build());

            _community.AddMember(7, "AlexBond", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(7).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(9255468350667101753).SetCostumeRefId(16813567318560086134).SetLevel(60).SetPrestigeLevel(2))
                .Build());

            _community.AddMember(8, "Crypto137", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(8).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(421791326977791218).SetCostumeRefId(1195778722002966150).SetLevel(60).SetPrestigeLevel(2))
                .Build());

            _community.AddMember(9, "yn01", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(9).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(12534955053251630387).SetCostumeRefId(14506515434462517197).SetLevel(60).SetPrestigeLevel(2))
                .Build());

            _community.AddMember(10, "Gazillion", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(10).SetIsOnline(0).Build());

            _community.AddMember(11, "FriendlyLawyer", CircleId.__Nearby);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(11).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(12394659164528645362).SetCostumeRefId(2844257346122946366).SetLevel(99).SetPrestigeLevel(1))
                .Build());
            #endregion

            // Initialize
            OnEnterGameInitStashTabOptions();

            // TODO: Clean up gameplay options init for new players
            if (settings.ArchiveData.IsNullOrEmpty())
                _gameplayOptions.ResetToDefaults();
        }

        public override void OnUnpackComplete(Archive archive)
        {
            base.OnUnpackComplete(archive);

            foreach (PrototypeId invProtoRef in _unlockedInventoryList)
            {
                PlayerStashInventoryPrototype stashInvProto = GameDatabase.GetPrototype<PlayerStashInventoryPrototype>(invProtoRef);
                if (stashInvProto == null)
                {
                    Logger.Warn("OnUnpackComplete(): stashInvProto == null");
                    continue;
                }

                if (stashInvProto.IsPlayerStashInventory && IsUsingUnifiedStash == false && stashInvProto.ConvenienceLabel == InventoryConvenienceLabel.UnifiedStash)
                    continue;

                Inventory inventory = GetInventoryByRef(invProtoRef);
                if (inventory == null && AddInventory(invProtoRef) == false)
                    Logger.Warn($"OnUnpackComplete(): Failed to add inventory, invProtoRef={invProtoRef.GetName()}");
            }
        }

        protected override void BindReplicatedFields()
        {
            base.BindReplicatedFields();

            _avatarProperties.Bind(this, AOINetworkPolicyValues.AOIChannelOwner);
            _playerName.Bind(this, AOINetworkPolicyValues.AOIChannelParty | AOINetworkPolicyValues.AOIChannelOwner);
            _partyId.Bind(this, AOINetworkPolicyValues.AOIChannelParty | AOINetworkPolicyValues.AOIChannelOwner);
        }

        protected override void UnbindReplicatedFields()
        {
            base.UnbindReplicatedFields();

            _avatarProperties.Unbind();
            _playerName.Unbind();
            _partyId.Unbind();
        }

        public override bool Serialize(Archive archive)
        {
            bool success = base.Serialize(archive);

            if (archive.IsTransient)    // REMOVEME/TODO: Persistent missions
                success &= Serializer.Transfer(archive, ref _missionManager);

            success &= Serializer.Transfer(archive, ref _avatarProperties);

            if (archive.IsTransient)
            {
                success &= Serializer.Transfer(archive, ref _shardId);
                success &= Serializer.Transfer(archive, ref _playerName);
                success &= Serializer.Transfer(archive, ref _consoleAccountIds[0]);
                success &= Serializer.Transfer(archive, ref _consoleAccountIds[1]);
                success &= Serializer.Transfer(archive, ref _secondaryPlayerName);
                success &= Serializer.Transfer(archive, ref _matchQueueStatus);
                success &= Serializer.Transfer(archive, ref _emailVerified);
                success &= Serializer.Transfer(archive, ref _accountCreationTimestamp);

                if (archive.IsReplication)
                {
                    success &= Serializer.Transfer(archive, ref _partyId);
                    success &= GuildMember.SerializeReplicationRuntimeInfo(archive, ref _guildId, ref _guildName, ref _guildMembership);

                    // There is a string here that is always empty and is immediately discarded after reading, purpose unknown
                    string emptyString = string.Empty;
                    success &= Serializer.Transfer(archive, ref emptyString);
                    if (emptyString != string.Empty) Logger.Warn($"Serialize(): emptyString is not empty!");
                }
            }

            bool hasCommunityData = /* archive.IsPersistent || */ archive.IsMigration ||    // REMOVEME/TODO: Persistent communities
                (archive.IsReplication && archive.HasReplicationPolicy(AOINetworkPolicyValues.AOIChannelOwner));
            success &= Serializer.Transfer(archive, ref hasCommunityData);
            if (hasCommunityData)
                success &= Serializer.Transfer(archive, ref _community);

            // Unknown bool, always false
            bool unkBool = false;
            success &= Serializer.Transfer(archive, ref unkBool);
            if (unkBool) Logger.Warn($"Serialize(): unkBool is true!");

            success &= Serializer.Transfer(archive, ref _unlockedInventoryList);

            if (archive.IsMigration || (archive.IsReplication && archive.HasReplicationPolicy(AOINetworkPolicyValues.AOIChannelOwner)))
                success &= Serializer.Transfer(archive, ref _badges);

            success &= Serializer.Transfer(archive, ref _gameplayOptions);

            // REMOVEME/TODO?: It seems achievement state is not supposed to be saved within player archives?
            if (archive.IsPersistent || archive.IsMigration || (archive.IsReplication && archive.HasReplicationPolicy(AOINetworkPolicyValues.AOIChannelOwner)))
                success &= Serializer.Transfer(archive, ref _achievementState);

            success &= Serializer.Transfer(archive, ref _stashTabOptionsDict);

            return success;
        }

        public void InitializeMissionTrackerFilters()
        {
            foreach (PrototypeId filterRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<MissionTrackerFilterPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                var filterProto = GameDatabase.GetPrototype<MissionTrackerFilterPrototype>(filterRef);
                if (filterProto.DisplayByDefault)
                    Properties[PropertyEnum.MissionTrackerFilter, filterRef] = true;
            }
        }

        public override void EnterGame(EntitySettings settings = null)
        {
            SendMessage(NetMessageMarkFirstGameFrame.CreateBuilder()
                .SetCurrentservergametime((ulong)Game.CurrentTime.TotalMilliseconds)
                .SetCurrentservergameid(Game.Id)
                .SetGamestarttime((ulong)Game.StartTime.TotalMilliseconds)
                .Build());

            SendMessage(NetMessageServerVersion.CreateBuilder().SetVersion(Game.Version).Build());
            SendMessage(Game.LiveTuningData.GetLiveTuningUpdate());

            SendMessage(NetMessageLocalPlayer.CreateBuilder()
                .SetLocalPlayerEntityId(Id)
                .SetGameOptions(Game.GameOptions)
                .Build());

            SendMessage(NetMessageReadyForTimeSync.DefaultInstance);

            // Enter game to become added to the AOI
            base.EnterGame(settings);
        }

        public override void ExitGame()
        {
            SendMessage(NetMessageBeginExitGame.DefaultInstance);
            AOI.SetRegion(0, true);

            base.ExitGame();
        }

        public Region GetRegion()
        {
            // This shouldn't need any null checks, at least for now
            return AOI.Region;
        }

        /// <summary>
        /// Returns the name of the player for the specified <see cref="PlayerAvatarIndex"/>.
        /// </summary>
        public string GetName(PlayerAvatarIndex avatarIndex = PlayerAvatarIndex.Primary)
        {
            if ((avatarIndex >= PlayerAvatarIndex.Primary && avatarIndex < PlayerAvatarIndex.Count) == false)
                Logger.Warn("GetName(): avatarIndex out of range");

            if (avatarIndex == PlayerAvatarIndex.Secondary)
                return _secondaryPlayerName.Get();

            return _playerName.Get();
        }

        /// <summary>
        /// Returns the console account id for the specified <see cref="PlayerAvatarIndex"/>.
        /// </summary>
        public ulong GetConsoleAccountId(PlayerAvatarIndex avatarIndex)
        {
            if ((avatarIndex >= PlayerAvatarIndex.Primary && avatarIndex < PlayerAvatarIndex.Count) == false)
                return 0;

            return _consoleAccountIds[(int)avatarIndex];
        }

        /// <summary>
        /// Returns <see langword="true"/> if the inventory with the specified <see cref="PrototypeId"/> is unlocked for this <see cref="Player"/>.
        /// </summary>
        public bool IsInventoryUnlocked(PrototypeId invProtoRef)
        {
            if (invProtoRef == PrototypeId.Invalid)
                return Logger.WarnReturn(false, $"IsInventoryUnlocked(): invProtoRef == PrototypeId.Invalid");

            return _unlockedInventoryList.Contains(invProtoRef);
        }

        /// <summary>
        /// Unlocks the inventory with the specified <see cref="PrototypeId"/> for this <see cref="Player"/>.
        /// </summary>
        public bool UnlockInventory(PrototypeId invProtoRef)
        {
            if (GetInventoryByRef(invProtoRef) != null)
                return Logger.WarnReturn(false, $"UnlockInventory(): {GameDatabase.GetFormattedPrototypeName(invProtoRef)} already exists");

            if (_unlockedInventoryList.Contains(invProtoRef))
                return Logger.WarnReturn(false, $"UnlockInventory(): {GameDatabase.GetFormattedPrototypeName(invProtoRef)} is already unlocked");

            _unlockedInventoryList.Add(invProtoRef);

            if (AddInventory(invProtoRef) == false || GetInventoryByRef(invProtoRef) == null)
                return Logger.WarnReturn(false, $"UnlockInventory(): Failed to add {GameDatabase.GetFormattedPrototypeName(invProtoRef)}");

            if (Inventory.IsPlayerStashInventory(invProtoRef))
                StashTabInsert(invProtoRef, 0);

            // Send unlock to the client
            var inventoryUnlockMessage = NetMessageInventoryUnlock.CreateBuilder()
                .SetInvProtoId((ulong)invProtoRef)
                .Build();

            SendMessage(inventoryUnlockMessage);

            return true;
        }

        /// <summary>
        /// Returns <see cref="PrototypeId"/> values of all locked and/or unlocked stash tabs for this <see cref="Player"/>.
        /// </summary>
        public IEnumerable<PrototypeId> GetStashInventoryProtoRefs(bool getLocked, bool getUnlocked)
        {
            var playerProto = Prototype as PlayerPrototype;
            if (playerProto == null) yield break;
            if (playerProto.StashInventories == null) yield break;

            foreach (EntityInventoryAssignmentPrototype invAssignmentProto in playerProto.StashInventories)
            {
                if (invAssignmentProto.Inventory == PrototypeId.Invalid) continue;

                bool isLocked = GetInventoryByRef(invAssignmentProto.Inventory) == null;

                if (isLocked && getLocked || isLocked == false && getUnlocked)
                    yield return invAssignmentProto.Inventory;
            }
        }

        /// <summary>
        /// Updates <see cref="StashTabOptions"/> with the data from a <see cref="NetMessageStashTabOptions"/>.
        /// </summary>
        public bool UpdateStashTabOptions(NetMessageStashTabOptions optionsMessage)
        {
            PrototypeId inventoryRef = (PrototypeId)optionsMessage.InventoryRefId;

            if (Inventory.IsPlayerStashInventory(inventoryRef) == false)
                return Logger.WarnReturn(false, $"UpdateStashTabOptions(): {inventoryRef} is not a player stash ref");

            if (GetInventoryByRef(inventoryRef) == null)
                return Logger.WarnReturn(false, $"UpdateStashTabOptions(): Inventory {GameDatabase.GetFormattedPrototypeName(inventoryRef)} not found");

            if (_stashTabOptionsDict.TryGetValue(inventoryRef, out StashTabOptions options) == false)
            {
                options = new();
                _stashTabOptionsDict.Add(inventoryRef, options);
            }

            // Stash tab names can be up to 30 characters long
            if (optionsMessage.HasDisplayName)
            {
                string displayName = optionsMessage.DisplayName;
                if (displayName.Length > 30)
                    displayName = displayName.Substring(0, 30);
                options.DisplayName = displayName;
            }

            if (optionsMessage.HasIconPathAssetId)
                options.IconPathAssetId = (AssetId)optionsMessage.IconPathAssetId;

            if (optionsMessage.HasColor)
                options.Color = (StashTabColor)optionsMessage.Color;

            return true;
        }

        /// <summary>
        /// Inserts the stash tab with the specified <see cref="PrototypeId"/> into the specified position.
        /// </summary>
        public bool StashTabInsert(PrototypeId insertedStashRef, int newSortOrder)
        {
            if (newSortOrder < 0)
                return Logger.WarnReturn(false, $"StashTabInsert(): Invalid newSortOrder {newSortOrder}");

            if (insertedStashRef == PrototypeId.Invalid)
                return Logger.WarnReturn(false, $"StashTabInsert(): Invalid insertedStashRef {insertedStashRef}");

            if (Inventory.IsPlayerStashInventory(insertedStashRef) == false)
                return Logger.WarnReturn(false, $"StashTabInsert(): insertedStashRef {insertedStashRef} is not a player stash ref");

            if (GetInventoryByRef(insertedStashRef) == null)
                return Logger.WarnReturn(false, $"StashTabInsert(): Inventory {GameDatabase.GetFormattedPrototypeName(insertedStashRef)} not found");

            // Get options for the tab we need to insert
            if (_stashTabOptionsDict.TryGetValue(insertedStashRef, out StashTabOptions options))
            {
                // Only new tabs are allowed to be in the same location
                if (options.SortOrder == newSortOrder)
                    return Logger.WarnReturn(false, "StashTabInsert(): Inserting an existing tab at the same location");
            }
            else
            {
                // Create options of the tab if there are none yet
                options = new();
                _stashTabOptionsDict.Add(insertedStashRef, options);
            }

            // No need to sort if only have a single tab
            if (_stashTabOptionsDict.Count == 1)
                return true;

            // Assign the new sort order to the tab
            int oldSortOrder = options.SortOrder;
            options.SortOrder = newSortOrder;

            // Rearrange other tabs
            int sortIncrement, sortStart, sortFinish;

            if (oldSortOrder < newSortOrder)
            {
                // If the sort order is increasing we need to shift back everything in between
                sortIncrement = -1;
                sortStart = oldSortOrder;
                sortFinish = newSortOrder;
            }
            else
            {
                // If the sort order is decreasing we need to shift forward everything in between
                sortIncrement = 1;
                sortStart = newSortOrder;
                sortFinish = oldSortOrder;
            }

            // Fall back in case our sort order overflows for some reason
            SortedList<int, PrototypeId> sortedTabs = new();
            bool orderOverflow = false;

            // Update sort order for all tabs
            foreach (var kvp in _stashTabOptionsDict)
            {
                PrototypeId sortRef = kvp.Key;
                StashTabOptions sortOptions = kvp.Value;

                if (sortRef != insertedStashRef)
                {
                    // Move the tab if:
                    // 1. We are adding a new tab and everything needs to be shifted
                    // 2. We are within our sort range
                    bool isNew = oldSortOrder == newSortOrder && sortOptions.SortOrder >= newSortOrder;
                    bool isWithinSortRange = sortOptions.SortOrder >= sortStart && sortOptions.SortOrder <= sortFinish;

                    if (isNew || isWithinSortRange)
                        sortOptions.SortOrder += sortIncrement;
                }

                // Make sure our sort order does not exceed the amount of stored stash tab options
                sortedTabs[sortOptions.SortOrder] = sortRef;
                if (sortOptions.SortOrder >= _stashTabOptionsDict.Count)
                    orderOverflow = true;
            }

            // Reorder if our sort order overflows
            if (orderOverflow)
            {
                Logger.Warn($"StashTabInsert(): Sort order overflow, reordering");
                int fixedOrder = 0;
                foreach (var kvp in sortedTabs)
                {
                    _stashTabOptionsDict[kvp.Value].SortOrder = fixedOrder;
                    fixedOrder++;
                }
            }

            return true;
        }

        public bool RevealInventory(PrototypeId inventoryProtoRef)
        {
            // Validate inventory prototype
            var inventoryPrototype = GameDatabase.GetPrototype<InventoryPrototype>(inventoryProtoRef);
            if (inventoryPrototype == null) return Logger.WarnReturn(false, "RevealInventory(): inventoryPrototype == null");

            // Skip reveal if this inventory does not require flagged visibility
            if (inventoryPrototype.InventoryRequiresFlaggedVisibility() == false)
                return true;

            // Validate inventory
            Inventory inventory = GetInventoryByRef(inventoryPrototype.DataRef);
            if (inventory == null) return Logger.WarnReturn(false, "RevealInventory(): inventory == null");

            // Skip reveal if already visible
            if (inventory.VisibleToOwner) return true;

            // Enable visibility
            inventory.VisibleToOwner = true;

            // Update interest for all contained entities
            foreach (var entry in inventory)
            {
                var entity = Game.EntityManager.GetEntity<Entity>(entry.Id);
                if (entity == null)
                {
                    Logger.Warn("RevealInventory(): entity == null");
                    continue;
                }

                AOI.ConsiderEntity(entity);
            }

            return true;
        }

        public override void OnOtherEntityAddedToMyInventory(Entity entity, InventoryLocation invLoc, bool unpackedArchivedEntity)
        {
            base.OnOtherEntityAddedToMyInventory(entity, invLoc, unpackedArchivedEntity);

            if (entity is Avatar avatar)
            {
                avatar.SetPlayer(this);

                if (invLoc.InventoryConvenienceLabel == InventoryConvenienceLabel.AvatarInPlay && invLoc.Slot == 0)
                    CurrentAvatar = avatar;
            }
        }

        public void OnChangeActiveAvatar(int avatarIndex, ulong lastCurrentAvatarId)
        {
            // TODO: Apply and remove avatar properties stored in the player

            SendMessage(NetMessageCurrentAvatarChanged.CreateBuilder()
                .SetAvatarIndex(avatarIndex)
                .SetLastCurrentEntityId(lastCurrentAvatarId)
                .Build());
        }

        public bool TrashItem(Item item)
        {
            // See CPlayer::RequestItemTrash for reference

            // Make sure this player is allowed to destroy this item
            if (item.PlayerCanDestroy(this) == false)
                return false;

            Avatar avatar = CurrentAvatar;

            // Make sure there is an avatar in the world
            if (avatar.IsInWorld == false)
                return false;

            // Destroy the item if it cannot be dropped
            if (item.WouldBeDestroyedOnDrop)
            {
                item.Destroy();
                return true;
            }

            // Drop item to the ground
            Region region = avatar.Region;

            // Find a position to drop
            if (region.ChooseRandomPositionNearPoint(avatar.Bounds, PathFlags.Walk, PositionCheckFlags.CanBeBlockedEntity,
                BlockingCheckFlags.CheckSpawns, 50f, 100f, out Vector3 dropPosition) == false)
            {
                // Fall back to avatar position if didn't find a free spot
                dropPosition = avatar.RegionLocation.Position;

                // Make sure we don't drop it somewhere where it can't be picked up (e.g. if the avatar is flying above something)
                if (region.NaviMesh.Contains(dropPosition, item.Bounds.Radius, new DefaultContainsPathFlagsCheck(PathFlags.Walk)) == false)
                    return false;
            }

            // Remove the item from its inventory (no going back now)
            item.ChangeInventoryLocation(null);

            // Drop it
            using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
            settings.OptionFlags |= EntitySettingsOptionFlags.IsNewOnServer;
            settings.SourceEntityId = avatar.Id;
            settings.SourcePosition = avatar.RegionLocation.Position;

            if (item.EnterWorld(region, dropPosition, Orientation.Zero, settings) == false)
            {
                item.Destroy();     // We have to destroy this item because it's no longer in player's inventory
                return Logger.WarnReturn(false, $"TrashItem(): Item {item} failed to enter world");
            }

            // Reapply lifespan
            TimeSpan expirationTime = item.GetExpirationTime();
            item.ResetLifespan(expirationTime);

            return true;
        }

        public bool CanAcquireCurrencyItem(WorldEntity entity)
        {
            if (entity.IsCurrencyItem() == false)
                return false;

            foreach (var kvp in entity.Properties.IteratePropertyRange(PropertyEnum.ItemCurrency))
            {
                Property.FromParam(kvp.Key, 0, out PrototypeId currencyProtoRef);
                CurrencyPrototype currencyProto = currencyProtoRef.As<CurrencyPrototype>();
                if (currencyProto == null)
                {
                    Logger.Warn("CanAcquireCurrencyItem(): currencyProto == null");
                    continue;
                }

                int currentAmount = Properties[PropertyEnum.ItemCurrency, currencyProtoRef];
                int delta = kvp.Value;

                if (currencyProto.MaxAmount > 0 && currentAmount + delta > currencyProto.MaxAmount)
                    return false;
            }

            return true;
        }

        public bool AcquireCurrencyItem(Entity entity)
        {
            if (entity.IsCurrencyItem() == false)
                return false;

            bool result = false;

            foreach (var kvp in entity.Properties.IteratePropertyRange(PropertyEnum.ItemCurrency))
            {
                int delta = kvp.Value * entity.CurrentStackSize;
                if (delta <= 0)
                    continue;

                Property.FromParam(kvp.Key, 0, out PrototypeId currencyProtoRef);
                CurrencyPrototype currencyProto = currencyProtoRef.As<CurrencyPrototype>();
                if (currencyProto == null)
                {
                    Logger.Warn("AcquireCurrencyItem(): currencyProto == null");
                    continue;
                }

                int currentAmount = Properties[PropertyEnum.ItemCurrency, currencyProtoRef];
                if (currencyProto.MaxAmount > 0 && currentAmount + delta > currencyProto.MaxAmount)
                    continue;

                Properties.AdjustProperty(delta, new(PropertyEnum.Currency, currencyProtoRef));
                result = true;
            }

            int runestonesAmount = entity.Properties[PropertyEnum.RunestonesAmount];
            if (runestonesAmount > 0)
            {
                Properties.AdjustProperty(runestonesAmount, new(PropertyEnum.RunestonesAmount));
                result = true;
            }

            return result;
        }

        protected override bool InitInventories(bool populateInventories)
        {
            bool success = base.InitInventories(populateInventories);

            PlayerPrototype playerProto = Prototype as PlayerPrototype;
            if (playerProto == null) return Logger.WarnReturn(false, "InitInventories(): playerProto == null");

            foreach (EntityInventoryAssignmentPrototype invEntryProto in playerProto.StashInventories)
            {
                var stashInvProto = invEntryProto.Inventory.As<PlayerStashInventoryPrototype>();
                if (stashInvProto == null)
                {
                    Logger.Warn("InitInventories(): stashInvProto == null");
                    continue;
                }

                if (stashInvProto.IsPlayerStashInventory && IsUsingUnifiedStash == false && stashInvProto.ConvenienceLabel == InventoryConvenienceLabel.UnifiedStash)
                    continue;

                if (stashInvProto.LockedByDefault == false)
                {
                    if (AddInventory(invEntryProto.Inventory) == false)
                    {
                        Logger.Warn($"InitInventories(): Failed to add inventory, invProtoRef={GameDatabase.GetPrototypeName(invEntryProto.Inventory)}");
                        success = false;
                    }
                }
            }

            return success;
        }

        /// <summary>
        /// Add the specified badge to this <see cref="Player"/>. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool AddBadge(AvailableBadges badge) => _badges.Add(badge);

        /// <summary>
        /// Removes the specified badge from this <see cref="Player"/>. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool RemoveBadge(AvailableBadges badge) => _badges.Remove(badge);

        /// <summary>
        /// Returns <see langword="true"/> if this <see cref="Player"/> has the specified badge.
        /// </summary>
        public bool HasBadge(AvailableBadges badge) => _badges.Contains(badge);

        public void AddTag(WorldEntity entity) => _tagEntities.Add(entity.Id);
        public void RemoveTag(WorldEntity entity) => _tagEntities.Remove(entity.Id);

        #region Avatar and Team-Up Management

        public Avatar GetAvatar(PrototypeId avatarProtoRef, AvatarMode avatarMode = AvatarMode.Normal)
        {
            if (avatarProtoRef == PrototypeId.Invalid) return Logger.WarnReturn<Avatar>(null, "GetAvatar(): avatarProtoRef == PrototypeId.Invalid");

            AvatarIterator iterator = new(this, AvatarIteratorMode.IncludeArchived, avatarProtoRef);

            return iterator.FirstOrDefault();
        }

        public Avatar GetActiveAvatarById(ulong avatarEntityId)
        {
            // TODO: Secondary avatar for consoles?
            return (CurrentAvatar.Id == avatarEntityId) ? CurrentAvatar : null;
        }

        public Avatar GetActiveAvatarByIndex(int index)
        {
            // TODO: Secondary avatar for consoles?
            return (index == 0) ? CurrentAvatar : null;
        }

        public Agent GetTeamUpAgent(PrototypeId teamUpProtoRef)
        {
            if (teamUpProtoRef == PrototypeId.Invalid) return Logger.WarnReturn<Agent>(null, "GetTeamUpAgent(): teamUpProtoRef == PrototypeId.Invalid");

            Inventory teamUpInv = GetInventory(InventoryConvenienceLabel.TeamUpLibrary);
            if (teamUpInv == null) return Logger.WarnReturn<Agent>(null, "GetTeamUpAgent(): teamUpInv == null");

            return teamUpInv.GetMatchingEntity(teamUpProtoRef) as Agent;
        }

        public bool IsTeamUpAgentUnlocked(PrototypeId teamUpRef)
        {
            return GetTeamUpAgent(teamUpRef) != null;
        }

        public void UnlockTeamUpAgent(PrototypeId teamUpRef)
        {
            if (IsTeamUpAgentUnlocked(teamUpRef)) return;

            var manager = Game?.EntityManager;
            if (manager == null) return;

            var teamUpProto = GameDatabase.GetPrototype<AgentTeamUpPrototype>(teamUpRef);
            if (teamUpProto == null) return;

            var inventory = GetInventory(InventoryConvenienceLabel.TeamUpLibrary);
            if (inventory == null) return;

            using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
            settings.InventoryLocation = new(Id, inventory.PrototypeDataRef);
            settings.EntityRef = teamUpRef;

            var teamUp = manager.CreateEntity(settings) as Agent;
            if (teamUp == null) return;

            teamUp.CombatLevel = 1;
            // TODO ExperiencePoints

            teamUp.Properties[PropertyEnum.PowerProgressionVersion] = teamUp.GetLatestPowerProgressionVersion();

            SendNewTeamUpAcquired(teamUpRef);
        }

        public bool BeginSwitchAvatar(PrototypeId avatarProtoRef)
        {
            Power avatarSwapChannel = CurrentAvatar.GetPower(GameDatabase.GlobalsPrototype.AvatarSwapChannelPower);
            if (avatarSwapChannel == null) return Logger.WarnReturn(false, "BeginSwitchAvatar(): avatarSwapChannel == null");

            PowerActivationSettings settings = new(CurrentAvatar.Id, CurrentAvatar.RegionLocation.Position, CurrentAvatar.RegionLocation.Position);
            settings.Flags = PowerActivationSettingsFlags.NotifyOwner;
            CurrentAvatar.ActivatePower(avatarSwapChannel.PrototypeDataRef, ref settings);

            Properties.RemovePropertyRange(PropertyEnum.AvatarSwitchPending);
            Properties[PropertyEnum.AvatarSwitchPending, avatarProtoRef] = true;

            return true;
        }

        public void ScheduleSwitchAvatarEvent()
        {
            // Schedule avatar switch at the end of the current frame to let switch power application finish first
            ScheduleEntityEvent(_switchAvatarEvent, TimeSpan.Zero);
        }

        public bool SwitchAvatar()
        {
            // Retrieve pending avatar proto ref recorded in properties
            PrototypeId avatarProtoRef = PrototypeId.Invalid;

            foreach (var kvp in Properties.IteratePropertyRange(PropertyEnum.AvatarSwitchPending))
            {
                Property.FromParam(kvp.Key, 0, out avatarProtoRef);
                break;
            }

            if (avatarProtoRef == PrototypeId.Invalid) return Logger.WarnReturn(false, "SwitchAvatar(): Failed to find pending avatar switch");
            Properties.RemovePropertyRange(PropertyEnum.AvatarSwitchPending);

            // Get information about the previous avatar
            ulong lastCurrentAvatarId = CurrentAvatar != null ? CurrentAvatar.Id : InvalidId;
            ulong prevRegionId = CurrentAvatar.RegionLocation.RegionId;
            Vector3 prevPosition = CurrentAvatar.RegionLocation.Position;
            Orientation prevOrientation = CurrentAvatar.RegionLocation.Orientation;

            // Do the switch
            Inventory avatarLibrary = GetInventory(InventoryConvenienceLabel.AvatarLibrary);
            Inventory avatarInPlay = GetInventory(InventoryConvenienceLabel.AvatarInPlay);

            if (avatarLibrary.GetMatchingEntity(avatarProtoRef) is not Avatar avatar)
                return Logger.WarnReturn(false, $"SwitchAvatar(): Failed to find avatar entity for avatarProtoRef {GameDatabase.GetPrototypeName(avatarProtoRef)}");

            InventoryResult result = avatar.ChangeInventoryLocation(avatarInPlay, 0);

            if (result != InventoryResult.Success)
                return Logger.WarnReturn(false, $"SwitchAvatar(): Failed to change library avatar's inventory location ({result})");

            EnableCurrentAvatar(true, lastCurrentAvatarId, prevRegionId, prevPosition, prevOrientation);
            return true;
        }

        public bool EnableCurrentAvatar(bool withSwapInPower, ulong lastCurrentAvatarId, ulong regionId, in Vector3 position, in Orientation orientation)
        {
            // TODO: Use this for teleportation within region as well

            if (CurrentAvatar == null)
                return Logger.WarnReturn(false, "EnableCurrentAvatar(): CurrentAvatar == null");

            if (CurrentAvatar.IsInWorld)
                return Logger.WarnReturn(false, "EnableCurrentAvatar(): Current avatar is already active");

            Region region = Game.RegionManager.GetRegion(regionId);
            if (region == null)
                return Logger.WarnReturn(false, "EnableCurrentAvtar(): region == null");

            Logger.Info($"EnableCurrentAvatar(): {CurrentAvatar} entering world");

            // Disable initial visibility and schedule swap-in power if requested
            using EntitySettings settings = ObjectPoolManager.Instance.Get<EntitySettings>();
            if (withSwapInPower)
            {
                settings.OptionFlags = EntitySettingsOptionFlags.IsClientEntityHidden;
                CurrentAvatar.ScheduleSwapInPower();
            }

            // Add new avatar to the world
            if (CurrentAvatar.EnterWorld(region, CurrentAvatar.FloorToCenter(position), orientation, settings) == false)
                return false;

            OnChangeActiveAvatar(0, lastCurrentAvatarId);
            return true;
        }

        public int GetLevelCapForCharacter(PrototypeId agentProtoRef)
        {
            // TODO
            return 60;
        }

        public void SetAvatarLibraryProperties()
        {
            if (CurrentAvatar == null)
            {
                // We should have a current avatar at this point
                Logger.Warn("SetAvatarLibraryProperties(): CurrentAvatar == null");
                return;
            }

            int maxAvatarLevel = 1;

            foreach (Avatar avatar in new AvatarIterator(this))
            {
                PrototypeId avatarProtoRef = avatar.PrototypeDataRef;

                // Library Level
                // NOTE: setting AvatarLibraryLevel above level 60 displays as prestige levels in the UI
                int characterLevel = avatar.Properties[PropertyEnum.CharacterLevel];
                maxAvatarLevel = Math.Max(characterLevel, maxAvatarLevel);
                Properties[PropertyEnum.AvatarLibraryLevel, 0, avatarProtoRef] = characterLevel;

                // Costume
                Properties[PropertyEnum.AvatarLibraryCostume, 0, avatarProtoRef] = avatar.Properties[PropertyEnum.CostumeCurrent];

                // Team-up
                Properties[PropertyEnum.AvatarLibraryTeamUp, 0, avatarProtoRef] = avatar.Properties[PropertyEnum.AvatarTeamUpAgent];

                // Unlock extra emotes
                Properties[PropertyEnum.AvatarEmoteUnlocked, avatarProtoRef, (PrototypeId)11651334702101696313] = true; // Powers/Emotes/EmoteCongrats.prototype
                Properties[PropertyEnum.AvatarEmoteUnlocked, avatarProtoRef, (PrototypeId)773103106671775187] = true;   // Powers/Emotes/EmoteDance.prototype
            }

            Properties[PropertyEnum.PlayerMaxAvatarLevel] = maxAvatarLevel;

            // TODO: Move mission manager somewhere else
            _missionManager.SetAvatar(CurrentAvatar.PrototypeDataRef);
        }

        public void OnAvatarCharacterLevelChanged(Avatar avatar)
        {
            int characterLevel = avatar.CharacterLevel;

            Properties[PropertyEnum.AvatarLibraryLevel, 0, avatar.PrototypeDataRef] = characterLevel;

            // Update max avatar level for things like mode unlocks
            if (characterLevel > Properties[PropertyEnum.PlayerMaxAvatarLevel])
                Properties[PropertyEnum.PlayerMaxAvatarLevel] = characterLevel;
        }

        public bool CanUseLiveTuneBonuses()
        {
            float serverBonusUnlockLevelOverride = LiveTuningManager.GetLiveGlobalTuningVar(GlobalTuningVar.eGTV_ServerBonusUnlockLevelOverride);
            int playerMaxAvatarLevel = Properties[PropertyEnum.PlayerMaxAvatarLevel];

            if (serverBonusUnlockLevelOverride != LiveTuningData.DefaultTuningVarValue)
                return playerMaxAvatarLevel >= serverBonusUnlockLevelOverride;

            // NOTE: ServerBonusUnlockLevel is set to 60 in 1.52.
            // TODO: Uncomment the real check when we no longer need to rely on live tuning for balancing rewards.
            //return playerMaxAvatarLevel >= GameDatabase.GlobalsPrototype.ServerBonusUnlockLevel;
            return true;
        }

        public bool CanChangeDifficulty(PrototypeId difficultyTierProtoRef)
        {
            DifficultyTierPrototype difficultyTierProto = difficultyTierProtoRef.As<DifficultyTierPrototype>();
            if (difficultyTierProto == null) return Logger.WarnReturn(false, "CanChangeDifficulty(): difficultyTierProto == null");

            // The game assumes all difficulties to be unlocked if there is no current avatar
            if (CurrentAvatar != null && CurrentAvatar.CharacterLevel < difficultyTierProto.UnlockLevel)
                return false;

            return true;
        }

        public PrototypeId GetDifficultyTierPreference()
        {
            // TODO: Party

            if (CurrentAvatar != null)
                return CurrentAvatar.Properties[PropertyEnum.DifficultyTierPreference];

            return GameDatabase.GlobalsPrototype.DifficultyTierDefault;
        }

        #endregion

        #region Loading and Teleports

        public void OnFullscreenMovieStarted(PrototypeId movieRef)
        {
            Logger.Trace($"OnFullscreenMovieStarted {GameDatabase.GetFormattedPrototypeName(movieRef)} for {_playerName}");
            var movieProto = GameDatabase.GetPrototype<FullscreenMoviePrototype>(movieRef);
            if (movieProto == null) return;
            if (movieProto.MovieType == MovieType.Cinematic)
                Properties[PropertyEnum.FullScreenMovieSession] = Game.Random.Next();
        }

        public void OnFullscreenMovieFinished(PrototypeId movieRef, bool userCancelled, uint syncRequestId)
        {
            // TODO syncRequestId ?
            Logger.Trace($"OnFullscreenMovieFinished {GameDatabase.GetFormattedPrototypeName(movieRef)} Canceled = {userCancelled} by {_playerName}");

            var movieProto = GameDatabase.GetPrototype<FullscreenMoviePrototype>(movieRef);
            if (movieProto == null) return;

            if (movieProto.MovieType == MovieType.Cinematic)
            {
                FullScreenMovieDequeued(movieRef);
                Properties.RemoveProperty(PropertyEnum.FullScreenMovieSession);
            }
        }

        private void FullScreenMovieDequeued(PrototypeId movieRef)
        {
            var propId = new PropertyId(PropertyEnum.FullScreenMovieQueued, movieRef);
            Properties.RemoveProperty(propId);
            if (Properties.HasProperty(PropertyEnum.FullScreenMovieQueued) == false)
                Properties.RemoveProperty(PropertyEnum.FullScreenMoviePlaying);
        }

        public void QueueFullscreenMovie(PrototypeId movieRef)
        {
            var movieProto = GameDatabase.GetPrototype<FullscreenMoviePrototype>(movieRef);
            if (movieProto == null) return;

            if (movieProto.MovieType == MovieType.Cinematic)
                FullScreenMovieQueued(movieRef);

            SendMessage(NetMessageQueueFullscreenMovie.CreateBuilder()
                .SetMoviePrototypeId((ulong)movieRef)
                .Build());
        }

        private void FullScreenMovieQueued(PrototypeId movieRef)
        {
            Properties[PropertyEnum.FullScreenMovieQueued, movieRef] = true;
            Properties[PropertyEnum.FullScreenMoviePlaying] = true;
        }

        public void QueueLoadingScreen(PrototypeId regionProtoRef)
        {
            IsOnLoadingScreen = true;

            SendMessage(NetMessageQueueLoadingScreen.CreateBuilder()
                .SetRegionPrototypeId((ulong)regionProtoRef)
                .Build());
        }

        public void QueueLoadingScreen(ulong regionId)
        {
            Region region = Game.RegionManager.GetRegion(regionId);
            PrototypeId regionProtoRef = region != null ? region.PrototypeDataRef : PrototypeId.Invalid;
            QueueLoadingScreen(regionProtoRef);
        }

        public void DequeueLoadingScreen()
        {
            SendMessage(NetMessageDequeueLoadingScreen.DefaultInstance);
        }

        public void OnLoadingScreenFinished()
        {
            IsOnLoadingScreen = false;
        }

        public void BeginTeleport(ulong regionId, in Vector3 position, in Orientation orientation)
        {
            _teleportData.Set(regionId, position, orientation);
            QueueLoadingScreen(regionId);
        }

        public void OnCellLoaded(uint cellId, ulong regionId)
        {
            AOI.OnCellLoaded(cellId, regionId);
            int numLoaded = AOI.GetLoadedCellCount();

            //Logger.Trace($"Player {this} loaded cell id={cellId} in region id=0x{regionId:X} ({numLoaded}/{AOI.TrackedCellCount})");

            if (_teleportData.IsValid && numLoaded == AOI.TrackedCellCount)
                FinishTeleport();
        }

        private bool FinishTeleport()
        {
            if (_teleportData.IsValid == false) return Logger.WarnReturn(false, "FinishTeleport(): No valid teleport data");

            EnableCurrentAvatar(false, CurrentAvatar.Id, _teleportData.RegionId, _teleportData.Position, _teleportData.Orientation);
            _teleportData.Clear();
            DequeueLoadingScreen();
            TryPlayIntroKismetSeqForRegion(CurrentAvatar.RegionLocation.RegionId);

            return true;
        }

        #endregion

        #region AOI & Discovery

        public bool InterestedInEntity(Entity entity, AOINetworkPolicyValues interestFilter)
        {
            if (entity == null) return Logger.WarnReturn(false, "InterestedInEntity(): entity == null");

            if (entity.InterestReferences.IsPlayerInterested(this) == false)
                return false;

            return AOI.InterestedInEntity(entity.Id, interestFilter);
        }

        public MapDiscoveryData GetMapDiscoveryData(ulong regionId)
        {
            Region region = Game.RegionManager.GetRegion(regionId);
            if (region == null) return Logger.WarnReturn<MapDiscoveryData>(null, "GetMapDiscoveryData(): region == null");

            if (_mapDiscoveryDict.TryGetValue(regionId, out MapDiscoveryData mapDiscoveryData) == false)
            {
                mapDiscoveryData = new(region);
                _mapDiscoveryDict.Add(regionId, mapDiscoveryData);
            }

            return mapDiscoveryData;
        }

        public MapDiscoveryData GetMapDiscoveryDataForEntity(WorldEntity worldEntity)
        {
            Region region = worldEntity?.Region;
            if (region == null) return null;
            return GetMapDiscoveryData(region.Id);
        }

        public bool DiscoverEntity(WorldEntity worldEntity, bool updateInterest)
        {
            MapDiscoveryData mapDiscoveryData = GetMapDiscoveryDataForEntity(worldEntity);
            if (mapDiscoveryData == null) return Logger.WarnReturn(false, "DiscoverEntity(): mapDiscoveryData == null");

            if (mapDiscoveryData.DiscoverEntity(worldEntity) == false)
                return false;

            if (updateInterest)
                AOI.ConsiderEntity(worldEntity);

            return true;
        }

        public bool UndiscoverEntity(WorldEntity worldEntity, bool updateInterest)
        {
            MapDiscoveryData mapDiscoveryData = GetMapDiscoveryDataForEntity(worldEntity);
            if (mapDiscoveryData == null) return Logger.WarnReturn(false, "UndiscoverEntity(): mapDiscoveryData == null");

            if (mapDiscoveryData.UndiscoverEntity(worldEntity) == false)
                return false;

            if (updateInterest)
                AOI.ConsiderEntity(worldEntity);

            return true;
        }

        public bool IsEntityDiscovered(WorldEntity worldEntity)
        {
            MapDiscoveryData mapDiscoveryData = GetMapDiscoveryDataForEntity(worldEntity);
            return mapDiscoveryData != null && mapDiscoveryData.IsEntityDiscovered(worldEntity);
        }

        public bool SendMiniMapUpdate()
        {
            Logger.Trace($"SendMiniMapUpdate(): {this}");

            Region region = GetRegion();
            if (region == null) return Logger.WarnReturn(false, "SendMiniMapUpdate(): region == null");

            MapDiscoveryData mapDiscoveryData = GetMapDiscoveryData(region.Id);
            if (mapDiscoveryData == null) return Logger.WarnReturn(false, "SendMiniMapUpdate(): mapDiscoveryData == null");

            LowResMap lowResMap = mapDiscoveryData.LowResMap;
            if (lowResMap == null) return Logger.WarnReturn(false, "SendMiniMapUpdate(): lowResMap == null");

            SendMessage(ArchiveMessageBuilder.BuildUpdateMiniMapMessage(lowResMap));
            return true;
        }

        #endregion

        public override void OnDeallocate()
        {
            Game.EntityManager.RemovePlayer(this);
            base.OnDeallocate();
        }

        public bool TryPlayIntroKismetSeqForRegion(ulong regionId)
        {
            // TODO/REMOVEME: Remove this when we have working Kismet sequence triggers
            Region region = Game.RegionManager.GetRegion(regionId);
            if (region == null) return Logger.WarnReturn(false, "TryPlayIntroKismetSeqForRegion(): region == null");

            KismetSeqPrototypeId kismetSeqRef = (RegionPrototypeId)region.PrototypeDataRef switch
            {
                RegionPrototypeId.NPERaftRegion             => KismetSeqPrototypeId.RaftHeliPadQuinJetLandingStart,
                RegionPrototypeId.TimesSquareTutorialRegion => KismetSeqPrototypeId.Times01CaptainAmericaLanding,
                RegionPrototypeId.TutorialBankRegion        => KismetSeqPrototypeId.BlackCatEntrance,
                _ => 0
            };

            if (kismetSeqRef == 0)
                return false;

            PlayKismetSeq((PrototypeId)kismetSeqRef);
            return true;
        }

        public void PlayKismetSeq(PrototypeId kismetSeqRef)
        {
            SendPlayKismetSeq(kismetSeqRef);
        }

        public void OnPlayKismetSeqDone(PrototypeId kismetSeqPrototypeId)
        {
            if (kismetSeqPrototypeId == PrototypeId.Invalid) return;

            if ((KismetSeqPrototypeId)kismetSeqPrototypeId == KismetSeqPrototypeId.RaftHeliPadQuinJetLandingStart)
            {
                // TODO trigger by hotspot
                KismetSeqPrototypeId kismetSeqRef = KismetSeqPrototypeId.RaftHeliPadQuinJetDustoff;
                SendMessage(NetMessagePlayKismetSeq.CreateBuilder().SetKismetSeqPrototypeId((ulong)kismetSeqRef).Build());
            }
            else if ((KismetSeqPrototypeId)kismetSeqPrototypeId == KismetSeqPrototypeId.OpDailyBugleVultureKismet)
            {
                // TODO trigger by MissionManager
                foreach (var entity in CurrentAvatar.RegionLocation.Cell.Entities)
                    if ((KismetBosses)entity.PrototypeDataRef == KismetBosses.EGD15GVulture)
                        entity.Properties[PropertyEnum.Visible] = true;
            }
            else if ((KismetSeqPrototypeId)kismetSeqPrototypeId == KismetSeqPrototypeId.SinisterEntrance)
            {
                // TODO trigger by MissionManager
                foreach (var entity in CurrentAvatar.RegionLocation.Cell.Entities)
                    if ((KismetBosses)entity.PrototypeDataRef == KismetBosses.MrSinisterCH7)
                        entity.Properties[PropertyEnum.Visible] = true;
            }
        }

        public override string ToString()
        {
            return $"{base.ToString()}, dbGuid=0x{DatabaseUniqueId:X}";
        }

        protected override void BuildString(StringBuilder sb)
        {
            base.BuildString(sb);

            sb.AppendLine($"{nameof(_missionManager)}: {_missionManager}");
            sb.AppendLine($"{nameof(_avatarProperties)}: {_avatarProperties}");
            sb.AppendLine($"{nameof(_shardId)}: {_shardId}");
            sb.AppendLine($"{nameof(_playerName)}: {_playerName}");
            sb.AppendLine($"{nameof(_consoleAccountIds)}[0]: {_consoleAccountIds[0]}");
            sb.AppendLine($"{nameof(_consoleAccountIds)}[1]: {_consoleAccountIds[1]}");
            sb.AppendLine($"{nameof(_secondaryPlayerName)}: {_secondaryPlayerName}");
            sb.AppendLine($"{nameof(_matchQueueStatus)}: {_matchQueueStatus}");
            sb.AppendLine($"{nameof(_emailVerified)}: {_emailVerified}");
            sb.AppendLine($"{nameof(_accountCreationTimestamp)}: {Clock.UnixTimeToDateTime(_accountCreationTimestamp)}");
            sb.AppendLine($"{nameof(_partyId)}: {_partyId}");

            if (_guildId != GuildMember.InvalidGuildId)
            {
                sb.AppendLine($"{nameof(_guildId)}: {_guildId}");
                sb.AppendLine($"{nameof(_guildName)}: {_guildName}");
                sb.AppendLine($"{nameof(_guildMembership)}: {_guildMembership}");
            }

            sb.AppendLine($"{nameof(_community)}: {_community}");

            for (int i = 0; i < _unlockedInventoryList.Count; i++)
                sb.AppendLine($"{nameof(_unlockedInventoryList)}[{i}]: {GameDatabase.GetPrototypeName(_unlockedInventoryList[i])}");

            if (_badges.Any())
            {
                sb.Append($"{nameof(_badges)}: ");
                foreach (AvailableBadges badge in _badges)
                    sb.Append(badge.ToString()).Append(' ');
                sb.AppendLine();
            }

            sb.AppendLine($"{nameof(_gameplayOptions)}: {_gameplayOptions}");
            sb.AppendLine($"{nameof(_achievementState)}: {_achievementState}");

            foreach (var kvp in _stashTabOptionsDict)
                sb.AppendLine($"{nameof(_stashTabOptionsDict)}[{GameDatabase.GetFormattedPrototypeName(kvp.Key)}]: {kvp.Value}");
        }

        /// <summary>
        /// Initializes <see cref="StashTabOptions"/> for any stash tabs that are unlocked but don't have any options yet.
        /// </summary>
        private void OnEnterGameInitStashTabOptions()
        {
            foreach (PrototypeId stashRef in GetStashInventoryProtoRefs(false, true))
            {
                if (_stashTabOptionsDict.ContainsKey(stashRef) == false)
                    StashTabInsert(stashRef, 0);
            }
        }

        public void SetGameplayOptions(NetMessageSetPlayerGameplayOptions clientOptions)
        {
            GameplayOptions newOptions = new(clientOptions.OptionsData);
            Logger.Debug(newOptions.ToString());

            _gameplayOptions = newOptions;

            // TODO: Process new options
        }

        public bool IsTargetable(AlliancePrototype allianceProto)
        {
            Avatar avatar = PrimaryAvatar ?? SecondaryAvatar;
            if (avatar != null && allianceProto != null && allianceProto.IsFriendlyTo(avatar.Alliance)) return true;
            if (IsFullscreenObscured) return false;
            if (Properties[PropertyEnum.GracePeriod]) return false;
            return true;
        }

        public WorldEntity GetDialogTarget(bool validateTarget = false)
        {
            if (DialogTargetId == InvalidId) return null;
            var target = Game.EntityManager.GetEntity<WorldEntity>(DialogTargetId);
            if (validateTarget && ValidateDialogTarget(target, DialogInteractorId) == false) return null;
            return target;
        }

        public bool SetDialogTarget(ulong targetId, ulong interactorId)
        {
            if (targetId != InvalidId && interactorId != InvalidId)
            {
                var interactor = Game.EntityManager.GetEntity<WorldEntity>(interactorId);
                if (interactor == null || interactor.IsInWorld == false) return false;

                var target = Game.EntityManager.GetEntity<WorldEntity>(targetId);
                if (ValidateDialogTarget(target, interactorId) == false)
                    return Logger.WarnReturn(false, $"ValidateDialogTarget false for {target.PrototypeName} with {interactor.PrototypeName}");
            }

            DialogTargetId = targetId;
            DialogInteractorId = interactorId;

            return true;
        }

        private bool ValidateDialogTarget(WorldEntity target, ulong interactorId)
        {
            if (target == null || target.IsInWorld == false) return false;
            var interactor = Game.EntityManager.GetEntity<WorldEntity>(interactorId);
            if (interactor == null || interactor.IsInWorld == false) return false;
            if (interactor.InInteractRange(target, InteractionMethod.Use, false) == false) return false;
            if (DialogInteractorId != InvalidId && DialogInteractorId != interactorId) return false;
            return true;
        }

        public bool HasAvatarFullyUnlocked(PrototypeId avatarRef)
        {
            AvatarUnlockType unlockType = GetAvatarUnlockType(avatarRef);
            return unlockType != AvatarUnlockType.None && unlockType != AvatarUnlockType.Starter;
        }

        public AvatarUnlockType GetAvatarUnlockType(PrototypeId avatarRef)
        {
            var avatarProto = GameDatabase.GetPrototype<AvatarPrototype>(avatarRef);
            if (avatarProto == null) return AvatarUnlockType.None;
            AvatarUnlockType unlockType = (AvatarUnlockType)(int)Properties[PropertyEnum.AvatarUnlock, avatarRef];
            if (unlockType == AvatarUnlockType.None && avatarProto.IsStarterAvatar)
                return AvatarUnlockType.Starter;
            return unlockType;
        }

        public int GetCharacterLevelForAvatar(PrototypeId avatarRef, AvatarMode avatarMode)
        {
            int levelCap = Avatar.GetAvatarLevelCap();
            if (levelCap <= 0) return 0;
            int level = Properties[PropertyEnum.AvatarLibraryLevel, (int)avatarMode, avatarRef];
            if (level <= 0) return 0;
            level %= levelCap;
            return level == 0 ? levelCap : level;
        }

        public void SetActiveChapter(PrototypeId chapterRef)
        {
            Properties[PropertyEnum.ActiveMissionChapter] = chapterRef;
        }

        public bool ChapterIsUnlocked(PrototypeId chapterRef)
        {
            var avatar = CurrentAvatar;
            if (avatar == null) return false;
            return avatar.Properties[PropertyEnum.ChapterUnlocked, chapterRef];
        }

        public void UnlockChapter(PrototypeId chapterRef)
        {
            var avatar = CurrentAvatar;
            if (avatar == null) return;
            avatar.Properties[PropertyEnum.ChapterUnlocked, chapterRef] = true;
        }

        public void UnlockChapters()
        {
            foreach (PrototypeId chapterRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<ChapterPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var ChapterProto = GameDatabase.GetPrototype<ChapterPrototype>(chapterRef);
                if (ChapterProto.StartLocked == false)
                    UnlockChapter(chapterRef);
            }
        }

        public void LockWaypoint(PrototypeId waypointRef)
        {
            var waypointProto = GameDatabase.GetPrototype<WaypointPrototype>(waypointRef);
            if (waypointProto == null) return;

            PropertyCollection collection;
            if (waypointProto.IsAccountWaypoint)
                collection = Properties;
            else
                collection = CurrentAvatar.Properties;

            var propId = new PropertyId(PropertyEnum.Waypoint, waypointRef);
            if (collection[propId])
                SendOnWaypointUpdated();

            collection[propId] = false;
        }

        public void UnlockWaypoint(PrototypeId waypointRef)
        {
            var waypointProto = GameDatabase.GetPrototype<WaypointPrototype>(waypointRef);
            if (waypointProto == null) return;

            PropertyCollection collection;
            if (waypointProto.IsAccountWaypoint)
                collection = Properties;
            else
                collection = CurrentAvatar.Properties;

            var propId = new PropertyId(PropertyEnum.Waypoint, waypointRef);
            if (collection[propId] == false)
            {
                SendWaypointUnlocked();
                collection[propId] = true;
            }
        }

        public void UnlockWaypoints()
        {
            foreach (PrototypeId waypointRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<WaypointPrototype>(PrototypeIterateFlags.NoAbstract))
            {
                var waypointProto = GameDatabase.GetPrototype<WaypointPrototype>(waypointRef);
                if (waypointProto.StartLocked == false)
                    UnlockWaypoint(waypointRef);
            }
        }

        #region SendMessage

        public void SendMessage(IMessage message) => PlayerConnection?.SendMessage(message);

        public void SendPlayKismetSeq(PrototypeId kismetSeqRef)
        {
            SendMessage(NetMessagePlayKismetSeq.CreateBuilder()
                .SetKismetSeqPrototypeId((ulong)kismetSeqRef)
                .Build());
        }

        public void SendAIAggroNotification(PrototypeId bannerMessageRef, Agent aiAgent, Player targetPlayer, bool party = false)
        {
            if (party)
            {
                // TODO send to party members
            }
            else
            {
                var message = NetMessageAIAggroNotification.CreateBuilder()
                    .SetBannerMessageRef((ulong)bannerMessageRef)
                    .SetAiRef((ulong)aiAgent.PrototypeDataRef)
                    .SetPlayerId(targetPlayer.Id)
                    .Build();

                SendMessage(message);
            }
        }

        public void SendRegionRestrictedRosterUpdate(bool enabled)
        {
            var message = NetMessageRegionRestrictedRosterUpdate.CreateBuilder().SetEnabled(enabled).Build();
            SendMessage(message);
        }

        public void SendRegionAvatarSwapUpdate(bool enabled)
        {
            var message = NetMessageRegionAvatarSwapUpdate.CreateBuilder().SetEnabled(enabled).Build();
            SendMessage(message);
        }

        public void SendNewTeamUpAcquired(PrototypeId teamUpRef)
        {
            var message = NetMessageNewTeamUpAcquired.CreateBuilder().SetPrototypeId((ulong)teamUpRef).Build();
            SendMessage(message);
        }

        public void SendPlayStoryBanter(AssetId banterRef)
        {
            var message = NetMessagePlayStoryBanter.CreateBuilder().SetBanterAssetId((ulong)banterRef).Build();
            SendMessage(message);
        }

        private void SendWaypointUnlocked()
        {
            SendOnWaypointUpdated();
            if (InterestedInEntity(this, AOINetworkPolicyValues.AOIChannelOwner) == false) return;
            var message = NetMessageOnWaypointUpdated.CreateBuilder().SetIdPlayer(Id).Build();
            SendMessage(message);
        }

        public void SendOnWaypointUpdated()
        {
            if (InterestedInEntity(this, AOINetworkPolicyValues.AOIChannelOwner) == false) return;
            var message = NetMessageOnWaypointUpdated.CreateBuilder().SetIdPlayer(Id).Build();
            SendMessage(message);
        }

        public void SendRegionDifficultyChange(int dificultyIndex)
        {
            var message = NetMessageRegionDifficultyChange.CreateBuilder().SetDifficultyIndex((ulong)dificultyIndex).Build();
            SendMessage(message);
        }

        public void SendHUDTutorial(HUDTutorialPrototype hudTutorialProto)
        {
            var hudTutorialRef = PrototypeId.Invalid;
            if (hudTutorialProto != null) hudTutorialRef = hudTutorialProto.DataRef;
            var message = NetMessageHUDTutorial.CreateBuilder().SetHudTutorialProtoId((ulong)hudTutorialRef).Build();
            SendMessage(message);
        }

        public void SendOpenUIPanel(AssetId panelNameId)
        {
            if (panelNameId == AssetId.Invalid) return;
            string panelName = GameDatabase.GetAssetName(panelNameId);
            if (panelName == "Unknown") return;
            var message = NetMessageOpenUIPanel.CreateBuilder().SetPanelName(panelName).Build();
            SendMessage(message);
        }

        public void SendWaypointNotification(PrototypeId waypointRef, bool show = true)
        {
            if (waypointRef == PrototypeId.Invalid) return;
            var message = NetMessageWaypointNotification.CreateBuilder()
                .SetWaypointProtoId((ulong)waypointRef)
                .SetShow(show).Build();
            SendMessage(message);
        }

        public void SendStoryNotification(StoryNotificationPrototype storyNotification, PrototypeId missionRef = PrototypeId.Invalid)
        {
            if (storyNotification == null) return;

            var message = NetMessageStoryNotification.CreateBuilder();
            message.SetDisplayTextStringId((ulong)storyNotification.DisplayText);

            if (storyNotification.SpeakingEntity != PrototypeId.Invalid)
                message.SetSpeakingEntityPrototypeId((ulong)storyNotification.SpeakingEntity);

            message.SetTimeToLiveMS((uint)storyNotification.TimeToLiveMS);
            message.SetVoTriggerAssetId((ulong)storyNotification.VOTrigger);

            if (missionRef != PrototypeId.Invalid)
                message.SetMissionPrototypeId((ulong)missionRef);

            SendMessage(message.Build());
        }

        public void SendBannerMessage(BannerMessagePrototype bannerMessage)
        {
            if (bannerMessage == null) return;
            var message = NetMessageBannerMessage.CreateBuilder()
                .SetBannerText((ulong)bannerMessage.BannerText)
                .SetTextStyle((ulong)bannerMessage.TextStyle)
                .SetTimeToLiveMS((uint)bannerMessage.TimeToLiveMS)
                .SetMessageStyle((uint)bannerMessage.MessageStyle)
                .SetDoNotQueue(bannerMessage.DoNotQueue)
                .SetShowImmediately(bannerMessage.ShowImmediately).Build();
            SendMessage(message);
        }

        #endregion

        public PrototypeId GetPublicEventTeam(PublicEventPrototype eventProto)
        {
            int eventInstance = eventProto.GetEventInstance();
            var teamProp = new PropertyId(PropertyEnum.PublicEventTeamAssignment, eventProto.DataRef, eventInstance);
            return Properties[teamProp];
        }

        public void SetTipSeen(PrototypeId tipDataRef)
        {
            if (tipDataRef == PrototypeId.Invalid) return;
            Properties[PropertyEnum.TutorialHasSeenTip, tipDataRef] = true;
        }

        public void ShowHUDTutorial(HUDTutorialPrototype hudTutorialProto)
        {
            if (hudTutorialProto != null && hudTutorialProto.ShouldShowTip(this) == false) return;

            if (CurrentHUDTutorial != hudTutorialProto)
            {
                var manager = Game.EntityManager;
                var inventory = GetInventory(InventoryConvenienceLabel.AvatarInPlay);
                if (inventory == null) return;

                bool send = hudTutorialProto != null;
                if (CurrentHUDTutorial != null)
                {
                    foreach (var entry in inventory)
                    {
                        var avatar = manager.GetEntity<Avatar>(entry.Id);
                        avatar?.ResetTutorialProps();
                    }
                    send |= CurrentHUDTutorial.CanDismiss == false && CurrentHUDTutorial.DisplayDurationMS <= 0;
                }
                if (send) SendHUDTutorial(hudTutorialProto);

                CurrentHUDTutorial = hudTutorialProto;

                if (hudTutorialProto != null)
                {
                    foreach (var entry in inventory)
                    {
                        var avatar = manager.GetEntity<Avatar>(entry.Id);
                        avatar?.SetTutorialProps(hudTutorialProto);
                    }

                    CancelScheduledHUDTutorialEvent();
                    if (hudTutorialProto.DisplayDurationMS > 0)
                        ScheduleEntityEvent(_hudTutorialResetEvent, TimeSpan.FromMilliseconds(hudTutorialProto.DisplayDurationMS));
                }
            }
        }

        private void CancelScheduledHUDTutorialEvent()
        {
            if (_hudTutorialResetEvent.IsValid)
            {
                var scheduler = Game.GameEventScheduler;
                scheduler.CancelEvent(_hudTutorialResetEvent);
            }
        }

        private void ResetHUDTutorial()
        {
            CancelScheduledHUDTutorialEvent();
            ShowHUDTutorial(null);
        }

        public void MissionInteractRelease(WorldEntity entity, PrototypeId missionRef)
        {
            if (missionRef == PrototypeId.Invalid) return;
            if (InterestedInEntity(entity, AOINetworkPolicyValues.AOIChannelOwner))
                SendMessage(NetMessageMissionInteractRelease.DefaultInstance);
        }

        private class ScheduledHUDTutorialResetEvent : CallMethodEvent<Entity>
        {
            protected override CallbackDelegate GetCallback() => (t) => (t as Player).ResetHUDTutorial();
        }

        private class SwitchAvatarEvent : CallMethodEvent<Entity>
        {
            protected override CallbackDelegate GetCallback() => (t) => ((Player)t).SwitchAvatar();
        }

        private struct TeleportData
        {
            public ulong RegionId { get; private set; }
            public Vector3 Position { get; private set; }
            public Orientation Orientation { get; private set; }

            public bool IsValid { get => RegionId != 0; }

            public void Set(ulong regionId, in Vector3 position, in Orientation orientation)
            {
                RegionId = regionId;
                Position = position;
                Orientation = orientation;
            }

            public void Clear()
            {
                RegionId = 0;
                Position = Vector3.Zero;
                Orientation = Orientation.Zero;
            }
        }
    }
}
