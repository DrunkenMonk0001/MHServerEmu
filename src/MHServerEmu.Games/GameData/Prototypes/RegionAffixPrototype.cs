﻿using MHServerEmu.Games.GameData.Calligraphy.Attributes;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.Regions;
using MHServerEmu.Core.Extensions;

namespace MHServerEmu.Games.GameData.Prototypes
{
    #region Enums

    [AssetEnum((int)None)]
    public enum MetaStateChallengeTierEnum
    {
        None = 0,
        Tier1 = 1,
        Tier2 = 2,
        Tier3 = 3,
        Tier4 = 4,
        Tier5 = 5,
    }

    #endregion

    public class RegionAffixPrototype : Prototype
    {
        public LocaleStringId Name { get; protected set; }
        public PrototypeId EnemyBoost { get; protected set; }
        public int Difficulty { get; protected set; }
        public PrototypeId AvatarPower { get; protected set; }
        public PrototypeId MetaState { get; protected set; }
        public MetaStateChallengeTierEnum ChallengeTier { get; protected set; }
        public int AdditionalLevels { get; protected set; }
        public PrototypeId Category { get; protected set; }
        public PrototypeId[] RestrictsAffixes { get; protected set; }
        public int UISortOrder { get; protected set; }
        public PrototypeId[] KeywordsBlacklist { get; protected set; }
        public PrototypeId[] KeywordsWhitelist { get; protected set; }
        public EnemyBoostEntryPrototype[] EnemyBoostsFiltered { get; protected set; }
        public PrototypeId[] AffixRarityRestrictions { get; protected set; }
        public EvalPrototype Eval { get; protected set; }

        [DoNotCopy]
        public KeywordsMask KeywordsBlackMask { get; protected set; }
        [DoNotCopy]
        public KeywordsMask KeywordsWhiteMask { get; protected set; }

        public override void PostProcess()
        {
            base.PostProcess();

            KeywordsBlackMask = KeywordPrototype.GetBitMaskForKeywordList(KeywordsBlacklist);
            KeywordsWhiteMask = KeywordPrototype.GetBitMaskForKeywordList(KeywordsWhitelist);
        }

        public bool CanApplyToRegion(Region region)
        {
            if (KeywordsBlacklist.HasValue())
                foreach (var area in region.Areas.Values)
                    if (area.Prototype.KeywordsMask.TestAny(KeywordsBlackMask))
                        return false;

            if (KeywordsWhitelist.HasValue())
            {
                foreach (var area in region.Areas.Values)
                    if (area.Prototype.KeywordsMask.TestAny(KeywordsWhiteMask))
                        return true;

                return false;
            }

            return true;
        }
    }

    public class RegionAffixTableTierEntryPrototype : Prototype
    {
        public PrototypeId LootTable { get; protected set; }
        public int Tier { get; protected set; }
        public LocaleStringId Name { get; protected set; }
    }

    public class RegionAffixWeightedEntryPrototype : Prototype
    {
        public PrototypeId Affix { get; protected set; }
        public int Weight { get; protected set; }
    }

    public class RegionAffixTablePrototype : Prototype
    {
        public EvalPrototype EvalTier { get; protected set; }
        public EvalPrototype EvalXPBonus { get; protected set; }
        public RegionAffixWeightedEntryPrototype[] RegionAffixes { get; protected set; }
        public RegionAffixTableTierEntryPrototype[] Tiers { get; protected set; }
        public AssetId LootSource { get; protected set; }

        public RegionAffixTableTierEntryPrototype GetByTier(int affixTier)
        {
            if (Tiers.IsNullOrEmpty()) return null;

            foreach (var entry in Tiers)
                if (entry != null && entry.Tier == affixTier)
                    return entry;

            return null;
        }
    }

    public class RegionAffixCategoryPrototype : Prototype
    {
        public int MaxPicks { get; protected set; }
        public int MinPicks { get; protected set; }
    }

    public class EnemyBoostEntryPrototype : Prototype
    {
        public PrototypeId EnemyBoost { get; protected set; }
        public PrototypeId[] RanksAllowed { get; protected set; }
        public PrototypeId[] RanksPrevented { get; protected set; }
    }
}
