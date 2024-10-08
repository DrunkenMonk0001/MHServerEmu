﻿using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Frontend;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Network;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("item", "Provides commands for creating items.")]
    public class ItemCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        [Command("drop", "Creates and drops the specified item from the current avatar. Optionally specify count.\nUsage: item drop [pattern] [count]")]
        public string Drop(string[] @params, FrontendClient client)
        {
            if (client == null) return "You can only invoke this command from the game.";
            if (@params.Length == 0) return "Invalid arguments. Type 'help item drop' to get help.";

            PrototypeId itemProtoRef = CommandHelper.FindPrototype(HardcodedBlueprints.Item, @params[0], client);
            if (itemProtoRef == PrototypeId.Invalid) return string.Empty;

            if (@params.Length == 1 || int.TryParse(@params[1], out int count) == false)
                count = 1;

            CommandHelper.TryGetPlayerConnection(client, out PlayerConnection playerConnection);
            Avatar avatar = playerConnection.Player.CurrentAvatar;

            LootManager lootGenerator = playerConnection.Game.LootManager;
            
            for (int i = 0; i < count; i++)
            {
                var item = lootGenerator.DropItem(avatar, itemProtoRef, 100f);
                Logger.Debug($"DropItem(): {item} from {avatar}");
            }

            return string.Empty;
        }

        [Command("give", "Creates and drops the specified item to the current player.\nUsage: item give [pattern]")]
        public string Give(string[] @params, FrontendClient client)
        {
            if (client == null) return "You can only invoke this command from the game.";
            if (@params.Length == 0) return "Invalid arguments. Type 'help item give' to get help.";

            PrototypeId itemProtoRef = CommandHelper.FindPrototype(HardcodedBlueprints.Item, @params[0], client);
            if (itemProtoRef == PrototypeId.Invalid) return string.Empty;

            CommandHelper.TryGetPlayerConnection(client, out PlayerConnection playerConnection);
            Player player = playerConnection.Player;

            LootManager lootGenerator = playerConnection.Game.LootManager;
            var item = lootGenerator.GiveItem(player, itemProtoRef);
            Logger.Debug($"GiveItem(): {item} to {player}");

            return string.Empty;
        }

        [Command("destroyindestructible", "Destroys indestructible items contained in the player's general inventory.\nUsage: item destroyindestructible")]
        public string DestroyIndestructible(string[] @params, FrontendClient client)
        {
            if (client == null) return "You can only invoke this command from the game.";

            CommandHelper.TryGetPlayerConnection(client, out PlayerConnection playerConnection);
            Player player = playerConnection.Player;
            Inventory general = player.GetInventory(InventoryConvenienceLabel.General);

            List<Item> indestructibleItemList = new();
            foreach (var entry in general)
            {
                Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                if (item == null) continue;

                if (item.ItemPrototype.CanBeDestroyed == false)
                    indestructibleItemList.Add(item);
            }

            foreach (Item item in indestructibleItemList)
                item.Destroy();

            return $"Destroyed {indestructibleItemList.Count} indestructible items.";
        }

        [Command("roll", "Test rolls a loot table.\nUsage: item testloottable", AccountUserLevel.Admin)]
        public string RollLootTable(string[] @params, FrontendClient client)
        {
            if (client == null) return "You can only invoke this command from the game.";

            CommandHelper.TryGetPlayerConnection(client, out PlayerConnection playerConnection);
            Player player = playerConnection.Player;

            //PrototypeId lootTableProtoRef = (PrototypeId)7277456960932484638;   // Loot/Tables/Mob/NormalMobs/PopcornSharedTable.prototype
            //PrototypeId lootTableProtoRef = (PrototypeId)10214339958427752538;  // Loot/Tables/Mob/NormalMobs/Chapter01/PopcornCh01Small.prototype
            //PrototypeId lootTableProtoRef = (PrototypeId)13573205868182115049;   // Loot/Tables/Mob/CowsAndKings/CowsLoot.prototype
            PrototypeId lootTableProtoRef = (PrototypeId)2972188768208229407;   // Loot/Tables/Mob/CowsAndKings/CowKingLoot.prototype

            player.Game.LootManager.TestLootTable(lootTableProtoRef, player);

            return string.Empty;
        }
    }
}
