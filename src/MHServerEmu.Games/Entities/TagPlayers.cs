﻿using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Entities
{
    public class TagPlayers
    {
        private EntityManager _manager;
        private WorldEntity _owner;
        private SortedSet<TagInfo> _tags;        

        public TagPlayers(WorldEntity worldEntity)
        {
            _owner = worldEntity;
            _manager = worldEntity.Game.EntityManager;
            _tags = new();
        }

        public IEnumerable<Player> GetPlayers()
        {
            ulong playerUid = 0;           
            foreach(var tag in _tags)
            {
                if (playerUid == tag.PlayerUID) continue;
                else playerUid = tag.PlayerUID;

                var player = _manager.GetEntityByDbGuid<Player>(playerUid);
                if (player != null)
                    yield return player;
            }
        }

        public void Add(Player player, PowerPrototype powerProto)
        {
            _tags.Add(new(player.DatabaseUniqueId, powerProto));
            player.AddTag(_owner);
        }
    }

    public struct TagInfo : IComparable<TagInfo>
    {
        public ulong PlayerUID;
        public PowerPrototype PowerPrototype;

        public TagInfo(ulong playerUID, PowerPrototype powerPrototype)
        {
            PlayerUID = playerUID;
            PowerPrototype = powerPrototype;
        }

        public int CompareTo(TagInfo other)
        {
            if (PlayerUID == other.PlayerUID && PowerPrototype == other.PowerPrototype) return 0;
            return PlayerUID.CompareTo(other.PlayerUID);
        }
    }
}
