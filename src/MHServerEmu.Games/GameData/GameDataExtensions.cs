﻿using MHServerEmu.Core.Extensions;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.GameData
{
    /// <summary>
    /// Provides shortcuts for access to game data.
    /// </summary>
    public static class GameDataExtensions
    {
        /// <summary>
        /// Returns the <see cref="AssetType"/> that this <see cref="AssetTypeId"/> refers to.
        /// </summary>
        public static AssetType AsAssetType(this AssetTypeId assetTypeId)
        {
            return GameDatabase.GetAssetType(assetTypeId);
        }

        /// <summary>
        /// Returns the <see cref="Curve"/> that this <see cref="CurveId"/> refers to.
        /// </summary>
        public static Curve AsCurve(this CurveId curveId)
        {
            return GameDatabase.GetCurve(curveId);
        }

        /// <summary>
        /// Returns the <see cref="Blueprint"/> that this <see cref="BlueprintId"/> refers to.
        /// </summary>
        public static Blueprint AsBlueprint(this BlueprintId blueprintId)
        {
            return GameDatabase.GetBlueprint(blueprintId);
        }

        /// <summary>
        /// Returns the <typeparamref name="T"/> that this <see cref="PrototypeId"/> refers to.
        /// </summary>
        public static T As<T>(this PrototypeId prototypeId) where T: Prototype
        {
            return GameDatabase.GetPrototype<T>(prototypeId);
        }

        /// <summary>
        /// Returns the name of this <see cref="AssetTypeId"/>.
        /// </summary>
        public static string GetName(this AssetTypeId assetTypeId)
        {
            return GameDatabase.GetAssetTypeName(assetTypeId);
        }

        /// <summary>
        /// Returns the name of this <see cref="AssetId"/>.
        /// </summary>
        public static string GetName(this AssetId assetId)
        {
            return GameDatabase.GetAssetName(assetId);
        }

        /// <summary>
        /// Returns the name of this <see cref="CurveId"/>.
        /// </summary>
        public static string GetName(this CurveId curveId)
        {
            return GameDatabase.GetCurveName(curveId);
        }

        /// <summary>
        /// Returns the name of this <see cref="BlueprintId"/>.
        /// </summary>
        public static string GetName(this BlueprintId blueprintId)
        {
            return GameDatabase.GetBlueprintName(blueprintId);
        }

        /// <summary>
        /// Returns the name of this <see cref="PrototypeId"/>.
        /// </summary>
        public static string GetName(this PrototypeId prototypeId)
        {
            return GameDatabase.GetPrototypeName(prototypeId);
        }

        /// <summary>
        /// Returns the formatted name of this <see cref="PrototypeId"/> (just the file name instead of the whole path).
        /// </summary>
        public static string GetNameFormatted(this PrototypeId prototypeId)
        {
            return GameDatabase.GetFormattedPrototypeName(prototypeId);
        }

        /// <summary>
        /// Returns <see langword="true"/> if this <see cref="PrototypeId"/> array shares any elements with another one.
        /// </summary>
        public static bool ShareElement(this PrototypeId[] protoRefs, PrototypeId[] otherProtoRefs)
        {
            if (protoRefs.IsNullOrEmpty() || otherProtoRefs.IsNullOrEmpty())
                return false;

            foreach (PrototypeId protoRef in protoRefs)
            {
                foreach (PrototypeId otherProtoRef in otherProtoRefs)
                {
                    if (protoRef == otherProtoRef)
                        return true;
                }
            }

            return false;
        }
    }
}
