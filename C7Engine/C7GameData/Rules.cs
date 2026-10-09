namespace C7GameData {
	public class Rules {
		// The number of internal movement units in one whole movement point,
		// Civ3's RULE.MovementAlongRoads. The original reads it at runtime and
		// scales both every unit's maximum movement and every cost by it, so a
		// scenario can change the movement scale; the shipped conquests.biq uses
		// 3, and the shipped scenarios range from 2 (Intro2 The Three Sisters) to
		// 4 (Scenarios/2 MP Rise of Rome) (11_movement.md §2.1).
		//
		// This is the default for a rules object that predates the field, so an
		// older save or ruleset still moves at the shipped rate.
		public const int DefaultMovementAlongRoads = 3;
		public int MovementAlongRoads = DefaultMovementAlongRoads;

		// Civ3's internal movement cost of a road step and a railroad step
		// (11_movement.md §3.2 and §3.3). The cost function charges the raw
		// constants 1 and 0 internal units, and only MovementAlongRoads converts
		// them into movement points at runtime. The step reads them from the rules
		// rather than from the save-carried per-improvement movementCost field, so
		// a save written before the scale existed (which carries the old fraction
		// 0.33333334 there) still moves at the shipped rate, and a scenario with a
		// different MovementAlongRoads still changes the step.
		public const int RoadStepInternalUnits = 1;
		public const int RailroadStepInternalUnits = 0;

		// The technology a city or colony tile's owner must know before the tile
		// participates in the road / railroad network at all (11_movement.md
		// section 4.2). The original's Tile_Check_Roads (0x5d9ff0) and
		// Tile_Check_Railroads (0x5da0d0) read the two fields at +0x1A4 and
		// +0x218 of the rules object reached through 0x9c7324 and hand them to
		// Leader_has_tech (0x561440). A city or colony tile whose owner does not
		// know the field's technology contributes no improvement, whatever its
		// raw overlay bit says; a tile without a city or colony uses the raw bit
		// unconditionally.
		//
		// The two fields are the Required technology of the Road and Railroad
		// terrain-improvement records - BIQ TFRM[3].Required and
		// TFRM[4].Required, at the record stride 0x74 that puts them at
		// rules+0x48+3*0x74 and rules+0x48+4*0x74. Measured in the shipped
		// conquests.biq the road is -1 and the railroad is 44 (Steam Power), so
		// the shipped road gate is inert and the shipped railroad gate is live.
		//
		// Both paths carry them: ImportTerraforms copies them out of the two
		// TFRM records, and C7/Lua/civ3/ruleset.json carries
		// cityRailroadRequiredTech. A null ID is the -1 case, because
		// Player.HasTech(null) is true, the same answer Leader_has_tech gives
		// for a tech id of -1.
		public ID CityRoadRequiredTech;
		public ID CityRailroadRequiredTech;

		public int MaximumResearchTime;
		public int MinimumResearchTime;

		// The base cost of each repeatable future technology. Measured across the
		// 26 parseable shipped BIQs it is 400 in the base rules, 600 in the two
		// Napoleonic Europe scenarios and 200 in Quick Civ, so a scenario must be
		// able to override it; 400 is the base-rules default (16_science.md
		// §4.2, §3.1).
		public int FutureTechCost = 400;
		public int ShieldValueInGold;
		public int ForestValueInShields;
		public int CitizenValueInShields;
		public int TurnPenaltyForEachHurrySacrifice;
		public int MaximumLevel1CitySize;
		public int MaximumLevel2CitySize;
		public int ChanceOfRioting;
		public int TurnPenaltyForEachDraftedCitizen;
		public int CitizensAffectedByEachHappyFace = 1;
		public int MinimumPopulationForWeLoveTheKing;
		public int FoodNeededToGrowForLevel1Cities = 20;
		public int FoodNeededToGrowForLevel2Cities = 40;
		public int FoodNeededToGrowForLevel3Cities = 60;
		public float BuildingDiscountForCivTraits = .5f;
		public string StartUnitType1;
		public string StartUnitType2;
		public string ScoutUnitType;
		public int MaxRankOfWorkableTiles;
		public int MaxRankOfBarbarianCampTiles;
		public int DefaultDealDuration;
		public float TreasuryInterestRate = .05f;
		public int MaxInterest = 50;
		public int ShieldCostPerGold;
		public float ShieldRateForDisbanding; // per cent
		public bool AllowLesserUnitProduction; // for example, allow building a Spearman/Pikeman when we can build a Musketman (simultaneously)
		public int RadarTileVisibility; // how many tiles, a unit with the Radar ability, can see ahead

		// When true, a goody hut may hand over a city ("an advanced tribe has
		// joined us"). This is the Civ3 "no city from a hut" rule toggle: bit
		// 0x400 of the toggleable-rules word, the bit the editor calls
		// "elimination", which the original reads for both meanings. The
		// shipped rules leave it clear. Carried on both paths: the BIQ import
		// (ImportRules) and C7/Lua/civ3/ruleset.json.
		public bool AllowCitiesFromGoodyHuts = true;
		// How many turns a Golden Age lasts (RULE.GoldenAgeDuration, measured as 20
		// in the shipped conquests.biq).
		public int GoldenAgeDuration = 20;

		// How many cities a civ must own per army it already has before it may
		// create another one (RULE.CitiesNeededToSupportAnArmy, measured as 4 in
		// the shipped conquests.biq).
		public int CitiesNeededToSupportAnArmy = 4;

		// Whether great scientific leaders can be created at all (GAME's "Allow
		// Scientific Leaders" toggle, bit 0x40000).
		public bool AllowScientificLeaders = true;
	}
}
