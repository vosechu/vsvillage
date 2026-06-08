using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VsVillage;

public enum EnumBuildingSize { Small, Medium, Large }

// One loaded + validated building schematic plus derived metadata.
public class BuildingDefinition
{
    public string Id;
    public string DisplayName;
    public string DisplayNameLangKey;
    public string TypeTag;           // "farmhouse", etc - parsed from filename, for category filtering
    public string FilenameSizeHint;  // small/medium/large from filename - cross-checked against dimension classification
    public EnumBuildingSize Size;
    public BlockSchematic Schematic;
    public int CostGears;
    public int DaysRequired;

    public int SizeX => Schematic?.SizeX ?? 0;
    public int SizeY => Schematic?.SizeY ?? 0;
    public int SizeZ => Schematic?.SizeZ ?? 0;
}

// Loads building schematics from assets/vsvillage/config/buildings/, validates each, sorts
// into size buckets, derives cost (gear-rusty) and days from block content. Exposed by id
// and by bucket for the marker GUI to query.
public class BuildingCatalog : ModSystem
{
    // Bucket caps. Schematics whose SizeX/Z or SizeY exceed all three buckets are rejected.
    public const int SmallMaxXZ = 7;
    public const int SmallMaxY  = 5;
    public const int MediumMaxXZ = 12;
    public const int MediumMaxY  = 12;
    public const int LargeMaxXZ = 24;
    public const int LargeMaxY  = 18;

    // Cost = round(materialScore / CostDivisor). Tune in tests.
    private const float CostDivisor = 5f;

    // One in-game day per N Y-layers of the schematic.
    private const int YLayersPerDay = 2;

    private readonly Dictionary<string, BuildingDefinition> _byId = new();
    private readonly Dictionary<EnumBuildingSize, List<BuildingDefinition>> _byBucket = new()
    {
        { EnumBuildingSize.Small,  new List<BuildingDefinition>() },
        { EnumBuildingSize.Medium, new List<BuildingDefinition>() },
        { EnumBuildingSize.Large,  new List<BuildingDefinition>() },
    };

    public IReadOnlyDictionary<string, BuildingDefinition> AllById => _byId;
    public IReadOnlyList<BuildingDefinition> InBucket(EnumBuildingSize size) => _byBucket[size];

    public BuildingDefinition Get(string id) => _byId.TryGetValue(id, out BuildingDefinition def) ? def : null;

    public override bool ShouldLoad(EnumAppSide forSide) => true;

    // Load on both sides so the client GUI can read the catalog directly without a server round-trip.
    // Asset files ship in the mod, so client has the same JSONs.
    public override void AssetsFinalize(ICoreAPI api)
    {
        base.AssetsFinalize(api);
        LoadAll(api);
    }

    // Builder schematics use the "builder_<type>_<size><instance>.json" naming standard. Anything
    // not starting with "builder_" is skipped so this folder can hold other schematics later.
    private const string BuilderPrefix = "builder_";

    private void LoadAll(ICoreAPI api)
    {
        List<IAsset> assets = api.Assets.GetManyInCategory("config", "buildings/", "vsvillage");
        if (assets == null || assets.Count == 0)
        {
            api.Logger.Notification("[VsVillage] BuildingCatalog: no schematics under assets/vsvillage/config/buildings/. Catalog will be empty.");
            return;
        }

        int ok = 0, bad = 0, skipped = 0;
        foreach (IAsset asset in assets)
        {
            string id = asset.Name.Replace(".json", "");
            if (!id.StartsWith(BuilderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            BuildingDefinition def = TryLoadOne(api, asset, id);
            if (def != null)
            {
                _byId[id] = def;
                _byBucket[def.Size].Add(def);
                ok++;
            }
            else bad++;
        }
        api.Logger.Notification("[VsVillage] BuildingCatalog loaded {0} schematics ({1} rejected, {2} skipped by prefix). Small={3}, Medium={4}, Large={5}.",
            ok, bad, skipped,
            _byBucket[EnumBuildingSize.Small].Count,
            _byBucket[EnumBuildingSize.Medium].Count,
            _byBucket[EnumBuildingSize.Large].Count);
    }

    // Parses "builder_<type>_<size><instance>" e.g. "builder_farmhouse_medium1" into
    // (type="farmhouse", sizeHint="medium", instance="1"). Returns nulls on mismatch.
    private static readonly Regex FilenameRegex = new(
        @"^builder_(?<type>[a-z0-9]+)_(?<size>small|medium|large)(?<instance>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (string type, string sizeHint, string instance) ParseFilename(string id)
    {
        Match m = FilenameRegex.Match(id);
        if (!m.Success) return (null, null, null);
        return (m.Groups["type"].Value.ToLowerInvariant(),
                m.Groups["size"].Value.ToLowerInvariant(),
                m.Groups["instance"].Value);
    }

    // Concatenated type + instance for the GUI when no lang key matches.
    // builder_farmhouse_medium1 -> "Farmhouse1". The size word in slot 3 is dropped.
    private static string PrettyDisplayName(string type, string instance)
    {
        if (string.IsNullOrEmpty(type)) return null;
        string typed = char.ToUpperInvariant(type[0]) + type.Substring(1);
        return string.IsNullOrEmpty(instance) ? typed : $"{typed}{instance}";
    }

    private BuildingDefinition TryLoadOne(ICoreAPI api, IAsset asset, string id)
    {
        try
        {
            BlockSchematic schematic = JsonConvert.DeserializeObject<BlockSchematic>(asset.ToText());
            if (schematic == null)
            {
                api.Logger.Warning("[VsVillage] BuildingCatalog: {0} failed to deserialize.", id);
                return null;
            }

            EnumBuildingSize? bucket = ClassifyBucket(schematic);
            if (bucket == null)
            {
                api.Logger.Warning("[VsVillage] BuildingCatalog: {0} ({1}x{2}x{3}) exceeds largest bucket. Skipped.",
                    id, schematic.SizeX, schematic.SizeY, schematic.SizeZ);
                return null;
            }

            (string type, string sizeHint, string instance) = ParseFilename(id);

            if (sizeHint != null && !string.Equals(sizeHint, bucket.Value.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                api.Logger.Warning("[VsVillage] BuildingCatalog: {0} filename hints '{1}' but dimensions classify as '{2}'. Using dimensions.",
                    id, sizeHint, bucket.Value);
            }

            int days = Math.Max(1, (int)Math.Ceiling(schematic.SizeY / (float)YLayersPerDay));
            int cost = Math.Max(1, (int)Math.Round(ComputeMaterialScore(schematic, api) / CostDivisor));

            string langKey = type != null && instance != null
                ? $"vsvillage:building-{type}-{sizeHint}-{instance}"
                : null;

            return new BuildingDefinition
            {
                Id = id,
                DisplayName = PrettyDisplayName(type, instance) ?? id,
                DisplayNameLangKey = langKey,
                TypeTag = type,
                FilenameSizeHint = sizeHint,
                Size = bucket.Value,
                Schematic = schematic,
                CostGears = cost,
                DaysRequired = days,
            };
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[VsVillage] BuildingCatalog: {0} threw on load: {1}\n{2}", id, ex.Message, ex.StackTrace);
            return null;
        }
    }

    private static EnumBuildingSize? ClassifyBucket(BlockSchematic s)
    {
        int maxXZ = Math.Max(s.SizeX, s.SizeZ);
        if (maxXZ <= SmallMaxXZ  && s.SizeY <= SmallMaxY)  return EnumBuildingSize.Small;
        if (maxXZ <= MediumMaxXZ && s.SizeY <= MediumMaxY) return EnumBuildingSize.Medium;
        if (maxXZ <= LargeMaxXZ  && s.SizeY <= LargeMaxY)  return EnumBuildingSize.Large;
        return null;
    }

    private static bool AllBlocksResolve(BlockSchematic s, ICoreAPI api)
    {
        foreach (var kv in s.BlockCodes)
        {
            if (api.World.GetBlock(kv.Value) == null) return false;
        }
        return true;
    }

    // Per-block weight by EnumBlockMaterial. Total summed across non-air cells then divided by CostDivisor.
    private static float ComputeMaterialScore(BlockSchematic s, ICoreAPI api)
    {
        Dictionary<int, float> weightByBlockId = new Dictionary<int, float>();
        foreach (var kv in s.BlockCodes)
        {
            Block b = api.World.GetBlock(kv.Value);
            if (b == null) continue;
            weightByBlockId[kv.Key] = WeightFor(b.BlockMaterial);
        }

        float total = 0f;
        for (int i = 0; i < s.BlockIds.Count; i++)
        {
            if (weightByBlockId.TryGetValue(s.BlockIds[i], out float w)) total += w;
        }
        return total;
    }

    private static float WeightFor(EnumBlockMaterial m) => m switch
    {
        EnumBlockMaterial.Metal   => 5.0f,
        EnumBlockMaterial.Ore     => 4.0f,
        EnumBlockMaterial.Glass   => 3.0f,
        EnumBlockMaterial.Ceramic => 2.0f,
        EnumBlockMaterial.Brick   => 2.0f,
        EnumBlockMaterial.Stone   => 1.5f,
        EnumBlockMaterial.Cloth   => 1.0f,
        EnumBlockMaterial.Wood    => 1.0f,
        EnumBlockMaterial.Leaves  => 1.0f,
        EnumBlockMaterial.Ice     => 0.5f,
        EnumBlockMaterial.Soil    => 0.4f,
        EnumBlockMaterial.Sand    => 0.4f,
        EnumBlockMaterial.Gravel  => 0.4f,
        EnumBlockMaterial.Snow    => 0.4f,
        EnumBlockMaterial.Plant   => 0.4f,
        EnumBlockMaterial.Water   => 0.2f,
        EnumBlockMaterial.Lava    => 0.2f,
        _                          => 0.2f,
    };
}
