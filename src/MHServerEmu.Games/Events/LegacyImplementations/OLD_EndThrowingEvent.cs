﻿using Gazillion;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Events.LegacyImplementations
{
    public class OLD_EndThrowingEvent : ScheduledEvent
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private PlayerConnection _playerConnection;
        private PrototypeId _powerId;

        public void Initialize(PlayerConnection playerConnection, PrototypeId powerId)
        {
            _playerConnection = playerConnection;
            _powerId = powerId;
        }

        public override bool OnTriggered()
        {
            Logger.Trace("Event EndThrowing");

            Avatar avatar = _playerConnection.Player.CurrentAvatar;

            // Remove throwable properties
            avatar.Properties.RemoveProperty(PropertyEnum.ThrowableOriginatorEntity);
            avatar.Properties.RemoveProperty(PropertyEnum.ThrowableOriginatorAssetRef);

            // Unassign throwable and throwable cancel powers
            Power throwablePower = avatar.GetThrowablePower();
            Power throwableCancelPower = avatar.GetThrowableCancelPower();

            if (throwablePower != null) avatar.UnassignPower(throwablePower.PrototypeDataRef);
            if (throwableCancelPower != null) avatar.UnassignPower(throwableCancelPower.PrototypeDataRef);

            if (GameDatabase.GetPrototypeName(_powerId).Contains("CancelPower"))
            {
                if (_playerConnection.ThrowableEntity != null)
                    _playerConnection.SendMessage(ArchiveMessageBuilder.BuildEntityCreateMessage(_playerConnection.ThrowableEntity, AOINetworkPolicyValues.AOIChannelProximity));
                Logger.Trace("Event RestoreThrowable");
            }
            else
            {
                _playerConnection.ThrowableEntity?.Kill(avatar.Id);
            }

            _playerConnection.ThrowableEntity = null;

            return true;
        }
    }
}