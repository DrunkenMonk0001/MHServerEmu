﻿using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Collections;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.PlayerManagement
{
    public class LoginQueueManager
    {
        private const ushort MuxChannel = 1;

        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly TimeSpan MinProcessInterval = TimeSpan.FromMilliseconds(PlayerManagerService.TargetTickTimeMS * 2);

        private readonly DoubleBufferQueue<IFrontendClient> _newClientQueue = new();
        private readonly Queue<IFrontendClient> _loginQueue = new();
        private readonly Queue<IFrontendClient> _highPriorityLoginQueue = new();

        private readonly PlayerManagerService _playerManagerService;

        private CooldownTimer _processTimer = new(MinProcessInterval);

        public LoginQueueManager(PlayerManagerService playerManagerService)
        {
            _playerManagerService = playerManagerService;
        }

        public void Update()
        {
            AcceptNewClients();
            ProcessLoginQueue();
        }

        public void EnqueueNewClient(IFrontendClient client)
        {
            _newClientQueue.Enqueue(client);
        }

        /// <summary>
        /// Accepts asynchronously added clients to the login queue.
        /// </summary>
        private void AcceptNewClients()
        {
            _newClientQueue.Swap();

            while (_newClientQueue.CurrentCount > 0)
            {
                IFrontendClient client = _newClientQueue.Dequeue();

                if (client.IsConnected == false)
                {
                    Logger.Warn($"AcceptNewClients(): Client [{client}] disconnected before being accepted to the login queue");
                    continue;
                }

                // The client doesn't send any pings while it's waiting in the login queue, so we need to suspend receive timeouts here
                client.SuspendReceiveTimeout();

                // High priority queue always ignores server capacity
                if (IsClientHighPriority(client))
                    _highPriorityLoginQueue.Enqueue(client);
                else
                    _loginQueue.Enqueue(client);

                Logger.Info($"Accepted client [{client}] into the login queue");
            }
        }

        /// <summary>
        /// Process clients waiting in a login queue.
        /// </summary>
        private void ProcessLoginQueue()
        {
            // Take short pauses between processing the login queue to avoid sending too many updates give the player manager time to register new clients
            if (_processTimer.Check() == false)
                return;

            int totalCapacity = _playerManagerService.Config.ServerCapacity;
            int availableCapacity = totalCapacity - _playerManagerService.ClientManager.PlayerCount;

            // Let clients from the high priority queue in first ignoring capacity
            while (_highPriorityLoginQueue.Count > 0)
            {
                IFrontendClient client = _highPriorityLoginQueue.Dequeue();
                ProcessQueuedClient(client, ref availableCapacity);
            }

            // Let clients from the normal login queue, check available capacity if enabled
            while (_loginQueue.Count > 0 && (totalCapacity <= 0 || availableCapacity > 0))
            {
                IFrontendClient client = _loginQueue.Dequeue();
                ProcessQueuedClient(client, ref availableCapacity);
            }

            // Send status updates to remaining players
            int playersInLine = _loginQueue.Count;
            if (playersInLine == 0)
                return;

            LoginQueueStatus.Builder statusBuilder = LoginQueueStatus.CreateBuilder()
                .SetNumberOfPlayersInLine((ulong)playersInLine);

            ulong placeInLine = 1;

            foreach (IFrontendClient client in _loginQueue)
                client.SendMessage(MuxChannel, statusBuilder.SetPlaceInLine(placeInLine++).Build());
        }

        private static bool IsClientHighPriority(IFrontendClient client)
        {
            // Users with elevated privileges (moderators / admins) have high priority
            if (((IDBAccountOwner)client).Account.UserLevel > AccountUserLevel.User)
                return true;

            // Add more cases as needed

            return false;
        }

        private static bool ProcessQueuedClient(IFrontendClient client, ref int availableCapacity)
        {
            if (client.IsConnected == false)
                return Logger.WarnReturn(false, $"ProcessQueuedClient(): Client [{client}] disconnected while waiting in the login queue");

            // Under normal circumstances the client should not be trying to proceed without receiving SessionEncryptionChanged.
            // However, if a malicious user modifies their client, it may try to skip ahead, so we need to verify this.
            ((ClientSession)client.Session).LoginQueuePassed = true;

            client.SendMessage(MuxChannel, SessionEncryptionChanged.CreateBuilder()
                .SetRandomNumberIndex(0)
                .SetEncryptedRandomNumber(ByteString.Empty)
                .Build());

            Logger.Info($"Client [{client}] passed the login queue");

            availableCapacity--;

            return true;
        }
    }
}
