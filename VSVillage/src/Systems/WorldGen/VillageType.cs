using System.Collections.Generic;
using Newtonsoft.Json;

namespace VsVillage;

public class VillageType
{
	[JsonProperty]
	public string Code;

	[JsonProperty]
	public string Names;

	[JsonProperty]
	public List<StructureGroup> StructureGroups = new List<StructureGroup>();

	[JsonProperty]
	public string StreetCode = "game:packeddirt";

	[JsonProperty]
	public string BridgeCode = "game:planks-aged-ns";

	[JsonProperty]
	public int Height = 2;

	[JsonProperty]
	public int Length = 2;

	[JsonProperty]
	public int MinTemp = -30;

	[JsonProperty]
	public int MaxTemp = 40;

	[JsonProperty]
	public float MinRain;

	[JsonProperty]
	public float MaxRain = 1f;

	[JsonProperty]
	public float MinForest;

	[JsonProperty]
	public float MaxForest = 1f;
}
