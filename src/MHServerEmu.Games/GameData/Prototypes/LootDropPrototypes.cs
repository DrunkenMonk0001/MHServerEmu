﻿using MHServerEmu.Core.Collections;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Loot;

namespace MHServerEmu.Games.GameData.Prototypes
{
    public class LootDropAgentPrototype : LootDropPrototype
    {
        public PrototypeId Agent { get; protected set; }

        protected internal override LootRollResult Roll(LootRollSettings settings, IItemResolver resolver)
        {
            // TODO (overriding this to reduce log spam)
            return LootRollResult.NoRoll;
        }
    }

    public class LootDropCharacterTokenPrototype : LootNodePrototype
    {
        public CharacterTokenType AllowedTokenType { get; protected set; }
        public CharacterFilterType FilterType { get; protected set; }
        public LootNodePrototype OnTokenUnavailable { get; protected set; }
    }

    public class LootDropClonePrototype : LootNodePrototype
    {
        public LootMutationPrototype[] Mutations { get; protected set; }
        public short SourceIndex { get; protected set; }
    }

    public class LootDropCreditsPrototype : LootNodePrototype
    {
        public CurveId Type { get; protected set; }
    }

    public class LootDropItemPrototype : LootDropPrototype
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public PrototypeId Item { get; protected set; }
        public LootMutationPrototype[] Mutations { get; protected set; }

        public override bool OnResultsEvaluation(Player player, WorldEntity source)
        {
            if (Item == PrototypeId.Invalid || DataDirectory.Instance.PrototypeIsA<CostumePrototype>(Item) == false)
                return Logger.WarnReturn(false, $"LootDropItemPrototype::OnResultsEvaluation() is only supported for Costumes!");

            // Unlock costume for costume closet (consoles / 1.53)
            // player.UnlockCostume(Item);

            return true;
        }

        protected internal override LootRollResult Roll(LootRollSettings settings, IItemResolver resolver)
        {
            if (Item == PrototypeId.Invalid)
                return LootRollResult.NoRoll;

            int numItems = NumMin == NumMax ? NumMin : resolver.Random.Next(NumMin, NumMax);

            return RollItem(Item.As<ItemPrototype>(), numItems, settings, resolver, Mutations);
        }
    }

    public class LootDropItemFilterPrototype : LootDropPrototype
    {
        public short ItemRank { get; protected set; }
        public EquipmentInvUISlot UISlot { get; protected set; }

        protected internal override LootRollResult Roll(LootRollSettings settings, IItemResolver resolver)
        {
            LootRollResult result = LootRollResult.NoRoll;

            if (NumMin < 1 || ItemRank < 0 || UISlot == EquipmentInvUISlot.Invalid)
                return result;

            AvatarPrototype usableAvatarProto = settings.UsableAvatar;

            RestrictionTestFlags restrictionFlags = RestrictionTestFlags.All;
            if (settings.DropChanceModifiers.HasFlag(LootDropChanceModifiers.IgnoreCooldown) ||
                settings.DropChanceModifiers.HasFlag(LootDropChanceModifiers.PreviewOnly))
            {
                restrictionFlags &= ~RestrictionTestFlags.Cooldown;
            }

            int numRolls = NumMin == NumMax ? NumMin : resolver.Random.Next(NumMin, NumMax + 1);

            for (int i = 0; i < numRolls; i++)
            {
                int level = resolver.ResolveLevel(settings.Level, settings.UseLevelVerbatim);
                AvatarPrototype resolvedAvatarProto = resolver.ResolveAvatarPrototype(usableAvatarProto, settings.ForceUsable, settings.UsablePercent);
                PrototypeId rollFor = resolvedAvatarProto != null ? resolvedAvatarProto.DataRef : PrototypeId.Invalid;

                Picker<Prototype> picker = new(resolver.Random);
                LootUtilities.BuildInventoryLootPicker(picker, rollFor, UISlot);

                if (picker.Empty())
                {
                    resolver.ClearPending();
                    return LootRollResult.Failure;
                }

                PrototypeId? rarityProtoRef = resolver.ResolveRarity(settings.Rarities, level, null);
                if (rarityProtoRef == PrototypeId.Invalid)
                {
                    resolver.ClearPending();
                    return LootRollResult.Failure;
                }

                ItemPrototype itemProto = null;

                using DropFilterArguments filterArgs = ObjectPoolManager.Instance.Get<DropFilterArguments>();
                DropFilterArguments.Initialize(filterArgs, itemProto, rollFor, level, rarityProtoRef.Value, ItemRank, UISlot, resolver.LootContext);

                if (LootUtilities.PickValidItem(resolver, picker, null, filterArgs, ref itemProto, RestrictionTestFlags.All, ref rarityProtoRef) == false)
                {
                    resolver.ClearPending();
                    return LootRollResult.Failure;
                }

                filterArgs.Rarity = rarityProtoRef.Value;
                filterArgs.ItemProto = itemProto;

                result |= resolver.PushItem(filterArgs, restrictionFlags, 1, null);

                if (result.HasFlag(LootRollResult.Failure))
                {
                    resolver.ClearPending();
                    return LootRollResult.Failure;
                }
            }

            return resolver.ProcessPending(settings) ? result : LootRollResult.Failure;
        }
    }

    public class LootDropPowerPointsPrototype : LootDropPrototype
    {
    }

    public class LootDropHealthBonusPrototype : LootDropPrototype
    {
    }

    public class LootDropEnduranceBonusPrototype : LootDropPrototype
    {
    }

    public class LootDropXPPrototype : LootNodePrototype
    {
        public CurveId XPCurve { get; protected set; }
    }

    public class LootDropRealMoneyPrototype : LootDropPrototype
    {
        public LocaleStringId CouponCode { get; protected set; }
        public PrototypeId TransactionContext { get; protected set; }
    }

    public class LootDropBannerMessagePrototype : LootNodePrototype
    {
        public PrototypeId BannerMessage { get; protected set; }
    }

    public class LootDropUsePowerPrototype : LootNodePrototype
    {
        public PrototypeId Power { get; protected set; }
    }

    public class LootDropPlayVisualEffectPrototype : LootNodePrototype
    {
        public AssetId RecipientVisualEffect { get; protected set; }
        public AssetId DropperVisualEffect { get; protected set; }
    }

    public class LootDropChatMessagePrototype : LootNodePrototype
    {
        public LocaleStringId ChatMessage { get; protected set; }
        public PlayerScope MessageScope { get; protected set; }
    }

    public class LootDropVanityTitlePrototype : LootNodePrototype
    {
        public PrototypeId VanityTitle { get; protected set; }
    }

    public class LootDropVendorXPPrototype : LootNodePrototype
    {
        public PrototypeId Vendor { get; protected set; }
        public int XP { get; protected set; }
    }
}
