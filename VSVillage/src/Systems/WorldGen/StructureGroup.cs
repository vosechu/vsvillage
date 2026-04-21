using System.Collections.Generic;
using Newtonsoft.Json;

namespace VsVillage;

public class StructureGroup
{
	[JsonProperty]
	public string Code;

	[JsonProperty]
	public EnumVillageStructureSize Size;

	[JsonProperty]
	public int MinStructuresPerVillage;

	[JsonProperty]
	public int MaxStructuresPerVillage;

	public List<WorldGenVillageStructure> MatchingStructures = new List<WorldGenVillageStructure>();
}
