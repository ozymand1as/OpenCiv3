using System.Collections.Generic;
using System.Linq;

namespace C7GameData.Save {
	public class SaveBuilding {
		public enum Flag {
			IsCenterOfEmpire,
			VeteranGroundUnits,
			VeteranSeaUnits,
			MustBeCoastal,
			MustBeNearRiver,
			IncreasesLuxuryTrade,
			ReducesCorruption,
			ForbiddenPalace,
			IncreasesShieldsInWater,
			IncreasesFoodInWater,
			IncreasesTradeInWater,
			AllowsCitySize2,
			AllowsCitySize3,
			DoublesCityGrowthRate,
			ProvidesWalls,
			CanOnlyBeBuiltInTowns,
			TreasuryEarnsInterest,
			RemovesPopulationPollution,
			ReducesBuildingPollution,
			Plus50PercentResearch,
			DoublesResearchOutput,
			AllowsSpyMissions,
			ResistantToBribery,
			IncreasesLeaderChance,
			AllowsBuildArmy,
			AllowsLargerArmies,
			RequiresVictoriousArmy,
			BuildSpaceshipParts,
			AllowsWaterTrade,
			AllowsAirTrade,
			SafeSeaTravel,
			// Doubles combat strength against barbarians for the owning civ
			// (The Great Wall in the shipped rules; 12_combat.md §2.2).
			DoubleCombatVsBarbarians,
			// The other two halves of the commerce multiplier (spec 14 §4.2
			// step 5): +50% luxury (BLDG+0xec bit 3) and +50% tax (bit 4). The
			// shipped rules put the tax flag on the Marketplace, Bank and Stock
			// Exchange, and set the luxury flag on nothing.
			Plus50PercentLuxury,
			Plus50PercentCommerce,
			// The Wealth build, which converts a city's net shields to gold
			// instead of producing an item (spec 14 §4.2 step 6).
			Capitalization,
			// The shield multiplier's replacement rule (spec 14 §4.1 step 3):
			// power plants never stack with each other, only the best one the
			// city can use counts. The shipped Coal/Hydro/Nuclear/Solar plants
			// carry it.
			ReplacesOtherBuildings,
			// The two sea-movement wonder features (11_movement.md §2.2),
			// BLDG's wonder-feature word bits 0x8 and 0x4000 (ITW_Plus_One_
			// Ship_Movement and ITW_Plus_Two_Ship_Movement in the C3X header).
			// A civ that owns a wonder with one of them moves its SEA units one
			// or two movement points further. Each flag is a boolean per civ -
			// the original compares the count of carriers to zero - so a second
			// 0x8 wonder adds nothing on top of the first.
			//
			// In the shipped conquests.biq the Great Lighthouse and Magellan's
			// Voyage both carry 0x8 and nothing carries 0x4000 (the Civilopedia
			// text for both wonders says "increased by one"); the shipped
			// Conquests do use 0x4000 (the Norse Saga, the Navigation School and
			// the Caracol Observatory, the last of which carries both bits).
			PlusOneShipMovement,
			PlusTwoShipMovement,
		}

		// The building's contribution to the naval power of the city that has it
		// (BLDG.NavalPower, +0xc8). A hostile city's summed naval power is the
		// threat it projects against a sea unit passing through its zone of
		// control (11_movement.md §6.2). The shipped rules set it on the Coastal
		// Fortress only, and an absent key means zero.
		public int navalPower;

		public class GreatWonderProperties {
			// The name of the building this building gives to every city in the
			// empire on on the continent (like the pyramids or the internet).
			public string buildingGainedInEveryCity;
			public string buildingGainedInEveryCityOnContinent;
		}

		public string name;
		public int shieldCost;
		public int populationCost;
		public ID requiredTech;
		public string requiredBuilding;
		public GreatWonderProperties? greatWonderProperties;
		public bool isSmallWonder;

		// The slot of the spaceship part this building completes, or -1 when the
		// building is not a spaceship part.
		public int spaceshipPart = -1;
		public int culturePerTurn;
		// The net content faces this building contributes to the city that has
		// it. A negative value means the building has more unhappy faces than
		// content faces, matching the existing convention.
		public int contentFacesInCity;
		// The net content faces this building contributes to every other city
		// of the owner (or every other city on the same continent when
		// continentalMoodEffects is set).
		public int contentFacesAllCities;
		public bool continentalMoodEffects;
		// The name of the building type whose content faces this great wonder
		// doubles. Null when this wonder does not double any happiness.
		public string doublesHappinessFor;
		// The name of the government a great wonder requires to have any
		// happiness effect. Null when the wonder has no government requirement.
		public string requiredGovernment;
		public double combatDefenseBonus;

		// The amount of pollution this building adds to its city each turn,
		// from the BIQ's BLDG.Pollution field.
		public int pollution;

		// The building's shield multiplier, in units of 25 % (the shipped
		// Factory is 2, i.e. +50 %), from the BIQ's BLDG.Production field
		// (spec 14 §4.1 step 3).
		public int production;
		public int maintenanceCost;
		public int iconRowIndex;
		public ID? renderedObsoleteBy;

		// Assorted boolean flags for the building. They're stored in this set
		// rather than as booleans to avoid bloating the json file.
		public HashSet<Flag> flags = new();

		// The set of traits this building has. Civilizations with a matching
		// trait get discounted production costs.
		public HashSet<Civilization.Trait> traits = new();

		public HashSet<string> requiredResources = [];

		// Paths to Lua functions 
		public SortedSet<string> onFinishedUnitProduction = [];
		public SortedSet<string> productionPrerequisites = [];
		public SortedSet<string> tileModifiers = [];

		public SaveBuilding() { }
	}
}
