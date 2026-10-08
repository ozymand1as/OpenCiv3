namespace C7GameData {
	public class Rules {
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
		// joined us"). This is the Civ3 "no city from a hut" rule toggle: the
		// shipped rules leave it enabled, and it is not yet imported from a BIQ.
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
