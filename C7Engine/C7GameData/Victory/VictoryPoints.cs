using C7GameData;

namespace C7Engine;

/// The Conquests victory-point awards (spec 25 section 4). Civ3 applies each
/// award only while the rules have the victory-point group turned on, so the
/// callers gate on GameData.VictoryPointsEnabled before adding anything.
///
/// The four categories implemented here are the ones the engine can already
/// observe. The formulas are the original's, including its integer division:
///
///   advance     = TECH.Cost x AdvancementCost
///   unit kill   = (PRTO.Cost / 10) x DefeatingOpposingUnitCost
///   great wonder = BLDG.Cost x WonderCost
///   city capture = city_population x CityConquestPopulation
///
/// Civ3 stores a building's cost in the BIQ at one tenth of its shield cost,
/// which is why a building's shield cost is divided by ten first - both for an
/// imported BIQ (where the importer has already multiplied by ten) and for a
/// ruleset building (where the json holds real shields).
///
/// Two of the six categories are deliberately not implemented, because the
/// engine has no path that can observe them:
///
/// * **Points by Unit Capture** (capturing or recapturing the special
///   "Princess" unit, `CapturingSpecialUnit`). Needs the flag-unit rules
///   (`TR_CAPTURE_THE_PRINCESS` / `TR_REVERSE_CAPTURE_THE_FLAG`), which the
///   engine does not model.
/// * **Points by Location** (each turn a unit occupies a victory-point tile,
///   `tile_value` per turn). The per-tile victory-point flag is not imported,
///   so no game OpenCiv3 can load has a victory location; the whole
///   scenario-things surface (BIQ `Wmap.VictoryPointLocation`, the tile flag,
///   its save round trip) would have to come first.
public static class VictoryPoints {

	public static int ForAdvance(Tech tech, VictoryConditions rules) {
		return tech.Cost * rules.AdvancementCost;
	}

	public static int ForUnitKill(UnitPrototype killedUnit, VictoryConditions rules) {
		return (killedUnit.shieldCost / 10) * rules.DefeatingOpposingUnitCost;
	}

	public static int ForGreatWonder(Building wonder, VictoryConditions rules) {
		return (wonder.shieldCost / 10) * rules.WonderCost;
	}

	public static int ForCityCapture(int cityPopulation, VictoryConditions rules) {
		return cityPopulation * rules.CityConquestPopulation;
	}
}
