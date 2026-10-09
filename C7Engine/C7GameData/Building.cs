using System;
using System.Collections.Generic;
using System.Linq;
using C7GameData.Save;
using C7Engine;
using C7Engine.Lua;

namespace C7GameData {
	public class Building : IProducible {
		public class GreatWonderProperties {
			// The building this building gives to every city in the empire on
			// on the continent (like the pyramids or the internet), if any.
			public Building buildingGainedInEveryCity;
			public Building buildingGainedInEveryCityOnContinent;
		}

		public string name { get; set; }
		public int shieldCost { get; set; }
		public int populationCost { get; set; } // Will always be equal to 0 in the Civ3 rule set

		// Filled in in SaveGame::ConvertBuildings
		public Tech requiredTech { get; set; }
		public Tech? renderedObsoleteBy;

		public Building requiredBuilding;

		List<Func<City, bool>> productionPrerequisites = [];
		public Action<MapUnit> onFinishedUnitProduction;
		public Action<Tile.Yield> tileModifier;

		// Filled in in SaveGame::ConvertBuildings. Non-null for great wonders.
		public GreatWonderProperties? greatWonderProperties;

		public bool isSmallWonder;

		// The slot of the spaceship part this building completes, or -1 when the
		// building is not a spaceship part. Completing it adds to the owner's
		// Player.spaceshipPartsBuilt, which the space-race victory reads.
		public int spaceshipPart { get; set; } = -1;

		// Whether this building opens spaceship-part production for its owner.
		// Civ3 gates the parts on the civ owning the improvement that carries
		// BLDG.BuildSpaceshipParts (the Apollo Program in the shipped rules),
		// so the rule follows the flag rather than the building's name.
		public bool buildsSpaceshipParts { get; set; }
		public bool isCenterOfEmpire;
		public bool increasesLuxuryTrade;
		public bool reducesCorruption;
		public bool isForbiddenPalace;
		public bool allowsCitySize2;
		public bool allowsCitySize3;
		public bool doublesCityGrowthRate;
		public bool providesWalls;
		public bool onlyUsefulInTowns;
		public StrengthBonus? combatDefenseBonus;
		public bool providesVeteranGroundUnits;
		public bool treasuryEarnsInterest;

		// The building adds 50% to the science produced by its city, or doubles
		// it. The two flags stack additively in halves, so a Library plus a
		// Copernicus' Observatory gives 2.5x, not 3x.
		public bool plus50PercentResearch;
		public bool doublesResearchOutput;

		// The luxury and tax halves of the same commerce multiplier (spec 14
		// §4.2 step 5). They stack additively with the research half inside a
		// single division, so Marketplace + Bank is 2x tax, not 2.25x.
		public bool plus50PercentLuxury;
		public bool plus50PercentCommerce;

		// The Wealth build: a city whose production is this building converts
		// its net shields into gold (spec 14 §4.2 step 6).
		public bool capitalization;
		// A civ that owns a wonder with this flag may use spies.
		public bool allowsSpyMissions;

		// A city with this improvement resists propaganda (the Initiate
		// Propaganda mission's penalty term).
		public bool resistantToBribery;
		// The small-wonder abilities that feed the leader/army subsystem. The
		// Heroic Epic raises the military leader chance, the Military Academy is
		// the (open item) gate on building armies, and the Pentagon raises an
		// army's capacity by one.
		public bool increasesLeaderChance;
		public bool allowsBuildArmy;
		public bool allowsLargerArmies;

		// The building may only be built by a civ whose army has won a battle
		// (LSF_HAS_VICTORIOUS_ARMY). The shipped rules set it on the Heroic Epic
		// and the Military Academy. It is imported as data only: the site that
		// consumes it was not located in the binary (spec 23 section 8.1), so
		// nothing gates on it yet.
		public bool requiresVictoriousArmy;

		// Whether the building lets its city send and receive trade over the
		// water (a Harbour) or through the air (an Airport).
		public bool allowsWaterTrade;
		public bool allowsAirTrade;

		// Whether the building is a wonder that lets its owner's sea trade
		// enter Sea tiles without the relevant advance (the Great Lighthouse).
		public bool safeSeaTravel;

		// Whether the owner's units fight barbarians with a doubled strength:
		// in the binary, a non-barbarian side adds a flat +100% on top of the
		// difficulty's AttackBonusAgainstBarbarians while it owns a wonder
		// with this flag (the Great Wall in the shipped rules; 12_combat.md
		// §2.2).
		public bool doublesCombatVsBarbarians;

		// The traits this building has. For a Great Wonder this is the set of
		// civ traits the wonder is associated with, which is what the Golden Age
		// wonder trigger checks.
		public HashSet<Civilization.Trait> traits = [];

		public int culturePerTurn = 0;
		public int maintenanceCost = 0;

		// The pollution added to the city each turn by this building.
		public int pollution = 0;

		// The building's shield multiplier, in units of 25 %: the shipped
		// Factory is 2 (+50 %). The multipliers stack additively in quarters,
		// and a building with replacesOtherBuildings only counts as the best
		// one whose required building the city also has (spec 14 §4.1 step 3).
		public int production = 0;
		public bool replacesOtherBuildings;

		// Whether this building caps the pollution the city generates, from
		// the ITF_Removes_Population_Pollution and
		// ITF_Reduces_Buildings_Pollution flags.
		public bool removesPopulationPollution;
		public bool reducesBuildingPollution;

		public int contentFacesInCity = 0;

		// The number of unhappy faces that become content in the city with this
		// building. Note that this is less powerful than other sources of
		// unhappiness, like drafting or poprushing, which converts happy faces
		// to sad faces.
		public int unhappyFacesInCity = 0;

		// The same two faces, but applied to every other city of the owner.
		// When continentalMoodEffects is set only the owner's other cities on
		// the same continent are counted (spec 15 §3.1).
		public int contentFacesAllCities = 0;
		public int unhappyFacesAllCities = 0;
		public bool continentalMoodEffects = false;

		// The building type whose content faces this wonder doubles (The Oracle
		// doubles Temples, the Sistine Chapel Cathedrals). Null for everything
		// else.
		public Building doublesHappinessFor;

		// The government a great wonder requires for its happiness effects to
		// apply. Null when the wonder has no government requirement.
		public Government requiredGovernment;

		public HashSet<Resource> requiredResources { get; set; } = [];

		public int iconRowIndex = 0;

		SaveBuilding dataSource;

		public Building(SaveBuilding building, GameData gameData) {
			dataSource = building;

			name = building.name;
			shieldCost = building.shieldCost;
			populationCost = building.populationCost;
			isSmallWonder = building.isSmallWonder;
			spaceshipPart = building.spaceshipPart;
			buildsSpaceshipParts = building.flags.Contains(SaveBuilding.Flag.BuildSpaceshipParts);
			culturePerTurn = building.culturePerTurn;
			maintenanceCost = building.maintenanceCost;
			iconRowIndex = building.iconRowIndex;
			pollution = building.pollution;
			production = building.production;
			replacesOtherBuildings = building.flags.Contains(SaveBuilding.Flag.ReplacesOtherBuildings);

			if (building.contentFacesInCity < 0) {
				unhappyFacesInCity = -building.contentFacesInCity;
			} else {
				contentFacesInCity = building.contentFacesInCity;
			}

			if (building.contentFacesAllCities < 0) {
				unhappyFacesAllCities = -building.contentFacesAllCities;
			} else {
				contentFacesAllCities = building.contentFacesAllCities;
			}

			continentalMoodEffects = building.continentalMoodEffects;

			if (building.combatDefenseBonus > 0) {
				combatDefenseBonus = new(name, building.combatDefenseBonus);
			}

			isCenterOfEmpire = building.flags.Contains(SaveBuilding.Flag.IsCenterOfEmpire);
			increasesLuxuryTrade = building.flags.Contains(SaveBuilding.Flag.IncreasesLuxuryTrade);
			reducesCorruption = building.flags.Contains(SaveBuilding.Flag.ReducesCorruption);
			isForbiddenPalace = building.flags.Contains(SaveBuilding.Flag.ForbiddenPalace);
			allowsCitySize2 = building.flags.Contains(SaveBuilding.Flag.AllowsCitySize2);
			allowsCitySize3 = building.flags.Contains(SaveBuilding.Flag.AllowsCitySize3);
			doublesCityGrowthRate = building.flags.Contains(SaveBuilding.Flag.DoublesCityGrowthRate);
			providesWalls = building.flags.Contains(SaveBuilding.Flag.ProvidesWalls);
			onlyUsefulInTowns = building.flags.Contains(SaveBuilding.Flag.CanOnlyBeBuiltInTowns);
			providesVeteranGroundUnits = building.flags.Contains(SaveBuilding.Flag.VeteranGroundUnits);
			treasuryEarnsInterest = building.flags.Contains(SaveBuilding.Flag.TreasuryEarnsInterest);
			removesPopulationPollution = building.flags.Contains(SaveBuilding.Flag.RemovesPopulationPollution);
			reducesBuildingPollution = building.flags.Contains(SaveBuilding.Flag.ReducesBuildingPollution);
			plus50PercentResearch = building.flags.Contains(SaveBuilding.Flag.Plus50PercentResearch);
			doublesResearchOutput = building.flags.Contains(SaveBuilding.Flag.DoublesResearchOutput);
			plus50PercentLuxury = building.flags.Contains(SaveBuilding.Flag.Plus50PercentLuxury);
			plus50PercentCommerce = building.flags.Contains(SaveBuilding.Flag.Plus50PercentCommerce);
			capitalization = building.flags.Contains(SaveBuilding.Flag.Capitalization);
			allowsSpyMissions = building.flags.Contains(SaveBuilding.Flag.AllowsSpyMissions);
			resistantToBribery = building.flags.Contains(SaveBuilding.Flag.ResistantToBribery);
			increasesLeaderChance = building.flags.Contains(SaveBuilding.Flag.IncreasesLeaderChance);
			allowsBuildArmy = building.flags.Contains(SaveBuilding.Flag.AllowsBuildArmy);
			allowsLargerArmies = building.flags.Contains(SaveBuilding.Flag.AllowsLargerArmies);
			requiresVictoriousArmy = building.flags.Contains(SaveBuilding.Flag.RequiresVictoriousArmy);
			allowsWaterTrade = building.flags.Contains(SaveBuilding.Flag.AllowsWaterTrade);
			allowsAirTrade = building.flags.Contains(SaveBuilding.Flag.AllowsAirTrade);
			safeSeaTravel = building.flags.Contains(SaveBuilding.Flag.SafeSeaTravel);
			doublesCombatVsBarbarians = building.flags.Contains(SaveBuilding.Flag.DoubleCombatVsBarbarians);
			traits = new HashSet<Civilization.Trait>(building.traits);

			if (building.greatWonderProperties != null) {
				greatWonderProperties = new();
			}

			LoadLuaFunctions(gameData);
		}

		[LuaMethod]
		public bool IsGreatWonder() {
			return this.greatWonderProperties != null;
		}

		public bool CanProduce(City city, HashSet<Resource> accessibleResources) {
			if (!city.owner.HasRequiredTechnology(this)) {
				return false;
			}

			if (renderedObsoleteBy != null && city.owner.knownTechs.Contains(renderedObsoleteBy.id)) {
				return false;
			}

			// A city may hold only one copy of an ordinary building, but Civ3
			// allows a second copy of a spaceship part - a scenario can require
			// more than one of a part, and the original caps the parts per civ
			// rather than per city. See CanProduceSpaceshipPart.
			if (spaceshipPart < 0 && city.GetBuildings().Exists(cityBuilding => cityBuilding.building == this)) {
				return false;
			}

			if (spaceshipPart >= 0 && !CanProduceSpaceshipPart(city)) {
				return false;
			}

			if (greatWonderProperties != null) {
				// We can't build a great wonder if it was already built.
				if (EngineStorage.gameData.GreatWondersBuilt.Contains(name)) {
					return false;
				}

				// We can't build a great wonder if another one of our cities is
				// building it.
				foreach (City c in city.owner.cities) {
					if (c.itemBeingProduced != null && c.itemBeingProduced.name == name) {
						return false;
					}
				}
			}

			// TODO: Add logic for wonders and the palace
			if (isSmallWonder || isCenterOfEmpire) {
				return false;
			}

			if (requiredBuilding != null &&
				!city.GetBuildings().Exists(cityBuilding => cityBuilding.building == this)) {
				return false;
			}

			if (!requiredResources.All(accessibleResources.Contains)) {
				return false;
			}

			if (!productionPrerequisites.All(func => func(city))) {
				return false;
			}

			return true;
		}

		// Civ3 decides whether a city may build a spaceship part in
		// Leader_can_build_city_improvement (0x56a2a0). Two rules apply, both
		// read off the rules data:
		//
		//  * The civ must own the improvement carrying BLDG.BuildSpaceshipParts
		//    (the Apollo Program in the shipped rules). The original counts the
		//    flag-bearing small wonders the leader owns, so the gate is per civ,
		//    not per city.
		//  * The part is capped per civ at General.SpaceshipPartsNeeded[part],
		//    counting the copies already built and the copies the civ's cities
		//    are currently producing. The original compares built + in
		//    production against the requirement; a part in production elsewhere
		//    therefore also counts. It uses == rather than >=, which would let a
		//    part become available again after over-building; the spec's victory
		//    test is built[i] >= required[i] (spec 25 section 2.6), so >= is the
		//    rule that is modelled here.
		private bool CanProduceSpaceshipPart(City city) {
			if (!city.owner.OwnsBuildingWhere(building => building.buildsSpaceshipParts)) {
				return false;
			}

			int built = spaceshipPart < city.owner.spaceshipPartsBuilt.Count
				? city.owner.spaceshipPartsBuilt[spaceshipPart]
				: 0;
			int inProduction = city.owner.cities.Count(
				c => c.itemBeingProduced != null && c.itemBeingProduced.name == name);
			int needed = EngineStorage.gameData.victoryConditions.SpaceshipPartsNeededFor(spaceshipPart);

			return built + inProduction < needed;
		}

		public int ShieldCost(HashSet<Civilization.Trait> civTraits, float costFactor) {
			foreach (Civilization.Trait trait in dataSource.traits) {
				if (civTraits.Contains(trait)) {
					return (int)(shieldCost * EngineStorage.gameData.rules.BuildingDiscountForCivTraits * costFactor);
				}
			}
			return (int)(shieldCost * costFactor);
		}

		public bool isGreatWonderObsolete(Player owner) {
			if (greatWonderProperties == null) {
				return false;
			}
			if (renderedObsoleteBy == null) {
				return false;
			}
			return owner.knownTechs.Contains(renderedObsoleteBy.id);
		}

		public SaveBuilding ToSaveBuilding() {
			return dataSource;
		}

		public override string ToString() {
			return name;
		}

		private void LoadLuaFunctions(GameData gameData) {
			BehaviorEngine luaEngine = gameData.luaBehaviorEngine;

			foreach (var path in dataSource.productionPrerequisites) {
				var rule = luaEngine.ImportFunc<Func<City, bool>>(path);
				productionPrerequisites.Add(rule);
			}

			foreach (var path in dataSource.onFinishedUnitProduction) {
				var handler = luaEngine.ImportFunc<Action<MapUnit>>(path);
				onFinishedUnitProduction += handler;
			}

			foreach (var path in dataSource.tileModifiers) {
				var modifier = luaEngine.ImportFunc<Action<Tile.Yield>>(path);
				tileModifier += modifier;
			}
		}
	}
}
