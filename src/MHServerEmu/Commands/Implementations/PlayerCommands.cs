﻿using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Powers.Conditions;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("player")]
    [CommandGroupDescription("Commands for managing player data for the invoker's account.")]
    public class PlayerCommands : CommandGroup
    {
        [Command("costume")]
        [CommandDescription("Changes costume for the current avatar.")]
        [CommandUsage("player costume [name|reset|default]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Costume(string[] @params, NetClient client)
        {
            PrototypeId costumeProtoRef;

            switch (@params[0].ToLower())
            {
                case "reset":
                    costumeProtoRef = PrototypeId.Invalid;
                    break;

                case "default": // This undoes visual updates for most heroes
                    costumeProtoRef = (PrototypeId)HardcodedBlueprints.Costume;
                    break;

                default:
                    var matches = GameDatabase.SearchPrototypes(@params[0], DataFileSearchFlags.SortMatchesByName | DataFileSearchFlags.CaseInsensitive, HardcodedBlueprints.Costume);

                    if (matches.Any() == false)
                        return $"Failed to find any costumes containing {@params[0]}.";

                    if (matches.Count() > 1)
                    {
                        CommandHelper.SendMessage(client, $"Found multiple matches for {@params[0]}:");
                        CommandHelper.SendMessages(client, matches.Select(match => GameDatabase.GetPrototypeName(match)), false);
                        return string.Empty;
                    }

                    costumeProtoRef = matches.First();
                    break;
            }

            PlayerConnection playerConnection = (PlayerConnection)client;
            var player = playerConnection.Player;
            var avatar = player.CurrentAvatar;

            avatar.ChangeCostume(costumeProtoRef);

            if (costumeProtoRef == PrototypeId.Invalid)
                return "Resetting costume.";

            return $"Changing costume to {GameDatabase.GetPrototypeName(costumeProtoRef)}.";
        }

        [Command("wipe")]
        [CommandDescription("Wipes all progress associated with the current account.")]
        [CommandUsage("player wipe [playerName]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Wipe(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            string playerName = playerConnection.Player.GetName();

            if (@params.Length == 0)
                return $"Type '!player wipe {playerName}' to wipe all progress associated with this account.\nWARNING: THIS ACTION CANNOT BE REVERTED.";

            if (string.Equals(playerName, @params[0], StringComparison.OrdinalIgnoreCase) == false)
                return "Incorrect player name.";

            playerConnection.WipePlayerData();
            return string.Empty;
        }

        [Command("givecurrency")]
        [CommandDescription("Gives all currencies.")]
        [CommandUsage("player givecurrency [amount]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string GiveCurrency(string[] @params, NetClient client)
        {
            if (int.TryParse(@params[0], out int amount) == false)
                return $"Failed to parse amount from {@params[0]}.";

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            foreach (PrototypeId currencyProtoRef in DataDirectory.Instance.IteratePrototypesInHierarchy<CurrencyPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                player.Properties.AdjustProperty(amount, new(PropertyEnum.Currency, currencyProtoRef));

            return $"Successfully given {amount} of all currencies.";
        }

        [Command("clearconditions")]
        [CommandDescription("Clears persistent conditions.")]
        [CommandUsage("player clearconditions")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string ClearConditions(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            Avatar avatar = player.CurrentAvatar;

            int count = 0;

            foreach (Condition condition in avatar.ConditionCollection)
            {
                if (condition.IsPersistToDB() == false)
                    continue;

                avatar.ConditionCollection.RemoveCondition(condition.Id);
                count++;
            }

            return $"Cleared {count} persistent conditions.";
        }
    }
}
