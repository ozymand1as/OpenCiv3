using System;
using System.Collections.Generic;

namespace C7GameData;

/// <summary>
/// Helpers over a game's era list. The list is rules data - the BIQ's ERAS
/// section, or ruleset.json's "eras" array for a game that has no BIQ - so
/// nothing here assumes a number of eras. A game whose rules carry no eras has
/// no era index at all: the lookups return -1 or null rather than falling back
/// to a built-in four-era table.
/// </summary>
public static class EraUtils {

	// The civilopedia keys of the eras the shipped rules carry. They name the
	// shipped ERAS records; they are references to that data, not the era
	// model, which is whatever list the loaded rules provide.
	public const string ANCIENT_TIMES_CVLPD = "ERAS_Ancient_Times";
	public const string MIDDLE_AGES_CVLPD = "ERAS_Middle_Ages";
	public const string INDUSTRIAL_AGE_CVLPD = "ERAS_Industrial_Age";
	public const string MODERN_ERA_CVLPD = "ERAS_Modern_Era";

	// The texture-registry keys the shipped UI registers its era art under
	// (C7/Lua/civ3/textures/tech_boxes.lua and the science advisor's
	// backgrounds): one per shipped era, in era order. A BIQ has no art field
	// for an era, so the import assigns these by the era's position in the
	// list, and ruleset.json can override each era's "artName".
	public static readonly string[] ShippedEraArtNames = { "ancient", "middle", "industrial", "modern" };

	// The art name a BIQ-imported era at this position gets. Eras beyond the
	// shipped art reuse the last one, the way the old four-era mapping clamped.
	public static string ShippedEraArtNameForIndex(int index) {
		if (index <= 0) {
			return ShippedEraArtNames[0];
		}
		return index < ShippedEraArtNames.Length ? ShippedEraArtNames[index] : ShippedEraArtNames[^1];
	}

	// The era with this civilopedia name, or null when the rules carry none.
	public static Era GetEra(IReadOnlyList<Era> eras, string civilopediaName) {
		if (eras == null) {
			return null;
		}
		foreach (Era era in eras) {
			if (era.civilopediaName == civilopediaName) {
				return era;
			}
		}
		return null;
	}

	// The era's position in the rules' list, or -1 when it is not in it.
	public static int GetEraIndex(IReadOnlyList<Era> eras, string civilopediaName) {
		if (eras == null) {
			return -1;
		}
		for (int i = 0; i < eras.Count; i++) {
			if (eras[i].civilopediaName == civilopediaName) {
				return i;
			}
		}
		return -1;
	}

	// The civilopedia name at an era position, clamped to the ends of the list:
	// the science advisor steps one era at a time and must stop at the first
	// and last. Null when the rules carry no eras.
	public static string EraIndexToEra(IReadOnlyList<Era> eras, int index) {
		if (eras == null || eras.Count == 0) {
			return null;
		}
		return eras[Math.Clamp(index, 0, eras.Count - 1)].civilopediaName;
	}

	public static string GetNiceEraName(IReadOnlyList<Era> eras, string civilopediaName) {
		Era era = GetEra(eras, civilopediaName);
		return era != null ? era.name : $"Not A Valid Era: {civilopediaName}";
	}

	public static string GetNextEraNameByIndex(IReadOnlyList<Era> eras, int index) {
		return EraIndexToEra(eras, index + 1);
	}

	public static string GetPreviousEraNameByIndex(IReadOnlyList<Era> eras, int index) {
		return EraIndexToEra(eras, index - 1);
	}

	public static string GetNextEraNiceName(IReadOnlyList<Era> eras, string civilopediaName) {
		int index = GetEraIndex(eras, civilopediaName);
		return index < 0 ? GetNiceEraName(eras, civilopediaName) : GetNiceEraName(eras, EraIndexToEra(eras, index + 1));
	}

	public static string GetPreviousEraNiceName(IReadOnlyList<Era> eras, string civilopediaName) {
		int index = GetEraIndex(eras, civilopediaName);
		return index < 0 ? GetNiceEraName(eras, civilopediaName) : GetNiceEraName(eras, EraIndexToEra(eras, index - 1));
	}

	// The texture-registry key for an era's art. The art itself is an art
	// choice with no BIQ equivalent, but it is resolved through the imported
	// era rather than a chain on the four shipped constants. An era the rules
	// do not describe, or an era the rules describe without art, falls back to
	// the shipped first-era art the old default used.
	public static string GetEraArtName(IReadOnlyList<Era> eras, string civilopediaName) {
		return GetEra(eras, civilopediaName)?.artName ?? ShippedEraArtNames[0];
	}
}
