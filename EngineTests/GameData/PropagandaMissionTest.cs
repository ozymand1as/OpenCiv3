using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

// The game state an Initiate Propaganda test needs: a real map, the shipped
// mission table and culture levels, both players related with a planted
// agent, and an acting civ that owns the Intelligence Agency wonder. The
// culture ratio is fixed at 300 (3000 against 1000) so the per-citizen base
// chance is always the "in awe of" level's 30.
internal static class PropagandaGame {
	public static TerrainType Grass() {
		return new TerrainType() {
			Key = "grassland",
			movementCost = 1,
			allowCities = true,
			height = 1,
			baseFoodProduction = 2,
			baseShieldProduction = 1,
			baseCommerceProduction = 1,
		};
	}

	public static C7GameData.GameData NewGameData(out Player actor, out Player target) {
		// timeOptions is what a loaded game always carries; the border-
		// ownership conflict resolver reads it through the static game data.
		C7GameData.GameData gd = new(customSeed: 7) { timeOptions = new TimeOptions() };
		// A 16x16 map with both cities away from the edge: a city whose border
		// reaches past the map pulls the static Tile.NONE into its border list,
		// and the border-conflict resolver then NREs on Tile.NONE.map.
		gd.map = CityCaptureTest.Game.MakeMap(16, 16);
		gd.rules = new Rules {
			MaxRankOfWorkableTiles = 2,
			MaximumLevel1CitySize = 6,
			MaximumLevel2CitySize = 12,
			ChanceOfRioting = 20,
			TurnPenaltyForEachDraftedCitizen = 20,
			TurnPenaltyForEachHurrySacrifice = 20,
			CitizensAffectedByEachHappyFace = 1,
			MinimumPopulationForWeLoveTheKing = 6,
		};
		gd.gameDifficulty = new Difficulty {
			NumberOfCitizensBornContent = 6,
			PercentageOfOptimalCities = 100,
			MilitaryLaw = 1,
		};
		gd.difficulties = new List<Difficulty> { gd.gameDifficulty };
		gd.citizenTypes = new List<CitizenType> {
			new() { IsDefaultCitizen = true, SingularName = "Laborer" },
			new() { SingularName = "Scientist", Research = 3, Luxuries = 1, Taxes = 2 },
		};
		gd.espionageMissions = EspionageTestGame.Missions();
		gd.cultureLevels = EspionageTestGame.CultureLevels();
		EngineStorage.InitializeGameDataForTests(gd);

		actor = NewPlayer(gd, "actor", "Actors");
		target = NewPlayer(gd, "target", "Targets");
		gd.players.Add(actor);
		gd.players.Add(target);
		EspionageTestGame.Meet(actor, target);
		// The spy mission menu requires a planted agent (spec 3.3).
		Relationship(actor, target).agentPlanted = true;

		Tile actorTile = gd.map.tileAt(5, 5);
		City actorCity = new(actorTile, actor, "Actors Town", gd.ids.CreateID("city"));
		actorCity.residents.Add(NewResident(gd, actor, actorCity, actor.civilization));
		actorCity.perPlayerCulture[actor] = 3000;
		actorTile.cityAtTile = actorCity;
		actor.cities.Add(actorCity);
		gd.cities.Add(actorCity);
		// The Intelligence Agency is the shipped wonder that unlocks the spy.
		actorCity.AddBuilding(EspionageTestGame.NewIntelligenceAgency(gd));

		return gd;
	}

	private static Player NewPlayer(C7GameData.GameData gd, string key, string civName) {
		return new Player {
			id = gd.ids.CreateID(key),
			civilization = new Civilization(civName),
			government = new Government { diplomatsAre = 1, spiesAre = 1 },
			gold = 100000,
			rules = gd.rules,
		};
	}

	private static PlayerRelationship Relationship(Player from, Player to) {
		return from.playerRelationships[to.id];
	}

	// A resident of the target city: `actorNationals` of its citizens are of
	// the acting civ's nationality (the +20 term of spec 6.6 step 2), the rest
	// belong to the city's owner. Every citizen works its own grass tile so
	// the happiness recompute has real yields to look at.
	private static CityResident NewResident(C7GameData.GameData gd, Player owner, City city, Civilization nationality) {
		Tile worked = new Tile(ID.None("tile")) {
			XCoordinate = -1,
			YCoordinate = -1,
			overlayTerrainType = Grass(),
		};
		return new CityResident {
			citizenType = gd.citizenTypes.Find(c => c.IsDefaultCitizen),
			nationality = nationality,
			city = city,
			tileWorked = worked,
		};
	}

	public static City AddTargetCity(C7GameData.GameData gd, Player actor, Player target, Tile tile, int population, int actorNationals) {
		City city = new(tile, target, "Target", gd.ids.CreateID("city"));
		for (int i = 0; i < population; i++) {
			Civilization nationality = i < actorNationals ? actor.civilization : target.civilization;
			city.residents.Add(NewResident(gd, target, city, nationality));
		}
		city.perPlayerCulture[target] = 1000;
		tile.cityAtTile = city;
		target.cities.Add(city);
		gd.cities.Add(city);
		return city;
	}

	// A unit on the city tile. The propaganda garrison count only asks for
	// attack or defence strength greater than zero (spec 6.6 step 1).
	public static MapUnit PlaceUnit(C7GameData.GameData gd, Player owner, Tile tile, int attack, int defense) {
		MapUnit unit = new MapUnit(gd.ids.CreateID("unit")) {
			name = "Unit",
			unitType = new UnitPrototype { name = "Unit", attack = attack, defense = defense },
			owner = owner,
			nationality = owner.civilization,
			location = tile,
		};
		unit.unitType.categories.Add("Land");
		tile.unitsOnTile.Add(unit);
		owner.units.Add(unit);
		return unit;
	}

	// The government names and the whole GOVT_GOVT BriberyModifier matrix of
	// the shipped conquests.biq, measured with a QueryCiv3 probe (rows are the
	// acting government, columns the target government, list order
	// Anarchy/Despotism/Monarchy/Communism/Republic/Democracy/Fascism/
	// Feudalism). PropagandaRulesDataTest asserts this set against the BIQ
	// import; the D-term tests use it to pin the row/column orientation.
	public static readonly string[] GovernmentNames = {
		"Anarchy", "Despotism", "Monarchy", "Communism",
		"Republic", "Democracy", "Fascism", "Feudalism",
	};
	public static readonly int[,] BriberyModifiers = {
		{ 0, 0, 0, 0, 0, 0, 0, 0 },
		{ 15, 0, -10, -15, -20, -30, 0, 0 },
		{ 20, 15, 0, -5, -10, -25, 0, 0 },
		{ 25, 20, 5, 0, -10, -20, 0, 0 },
		{ 30, 25, 10, 8, 0, -5, 0, 0 },
		{ 35, 30, 20, 10, 5, 0, 0, 0 },
		{ 0, 0, -5, -20, -10, -10, 0, 0 },
		{ 20, 15, 0, -10, -10, -20, -25, 0 },
	};

	// Installs the shipped government list, in file order, so the
	// government-pair term resolves like it does against a real ruleset.
	public static List<Government> InstallShippedGovernments(C7GameData.GameData gd) {
		gd.governments.Clear();
		for (int row = 0; row < GovernmentNames.Length; row++) {
			Government government = new() { name = GovernmentNames[row], immuneTo = -1 };
			for (int column = 0; column < GovernmentNames.Length; column++) {
				government.briberyModifiers.Add(BriberyModifiers[row, column]);
			}
			gd.governments.Add(government);
		}
		return gd.governments;
	}
}

// The penalty total D of spec 24 section 6.6 step 1, one test per term. Each
// test fails against the pre-change tree because Espionage.PropagandaPenalty
// does not exist there - mission 6 had no resolution path at all.
public class PropagandaPenaltyTest {
	private static (C7GameData.GameData gd, Player actor, Player target, City city, Tile tile) SetupCity() {
		C7GameData.GameData gd = PropagandaGame.NewGameData(out Player actor, out Player target);
		Tile tile = gd.map.tileAt(11, 11);
		City city = PropagandaGame.AddTargetCity(gd, actor, target, tile, population: 3, actorNationals: 0);
		return (gd, actor, target, city, tile);
	}

	[Fact]
	public void PenaltyIsZeroForABareCity() {
		(C7GameData.GameData gd, Player actor, Player target, City city, _) = SetupCity();

		Assert.Equal(0, Espionage.PropagandaPenalty(gd, actor, target, city));
	}

	[Fact]
	public void PenaltySubtractsFivePerMilitaryUnitAndIgnoresCivilians() {
		(C7GameData.GameData gd, Player actor, Player target, City city, Tile tile) = SetupCity();

		// A worker (attack 0, defence 0) is not a military unit.
		PropagandaGame.PlaceUnit(gd, target, tile, attack: 0, defense: 0);
		Assert.Equal(0, Espionage.PropagandaPenalty(gd, actor, target, city));

		// One warrior: -5.
		PropagandaGame.PlaceUnit(gd, target, tile, attack: 1, defense: 1);
		Assert.Equal(-5, Espionage.PropagandaPenalty(gd, actor, target, city));

		// A second unit with attack only also counts: -10.
		PropagandaGame.PlaceUnit(gd, target, tile, attack: 10, defense: 0);
		Assert.Equal(-10, Espionage.PropagandaPenalty(gd, actor, target, city));

		// Defence strength alone counts as well: -15.
		PropagandaGame.PlaceUnit(gd, target, tile, attack: 0, defense: 3);
		Assert.Equal(-15, Espionage.PropagandaPenalty(gd, actor, target, city));
	}

	[Fact]
	public void PenaltySubtractsFortyForTheOwnersCapital() {
		(C7GameData.GameData gd, Player actor, Player target, City city, _) = SetupCity();

		Assert.Equal(0, Espionage.PropagandaPenalty(gd, actor, target, city));
		city.capital = true;
		Assert.Equal(-40, Espionage.PropagandaPenalty(gd, actor, target, city));
	}

	[Fact]
	public void PenaltySubtractsTwentyForAResistantToBriberyImprovement() {
		(C7GameData.GameData gd, Player actor, Player target, City city, _) = SetupCity();

		// An improvement without the flag changes nothing.
		city.AddBuilding(new Building(new SaveBuilding { name = "Marketplace" }, gd));
		Assert.Equal(0, Espionage.PropagandaPenalty(gd, actor, target, city));

		// One Resistant to Bribery improvement is worth -20 ...
		city.AddBuilding(new Building(new SaveBuilding {
			name = "Courthouse",
			flags = { SaveBuilding.Flag.ResistantToBribery },
		}, gd));
		Assert.Equal(-20, Espionage.PropagandaPenalty(gd, actor, target, city));

		// ... and a second one does not stack: the rule counts the flag, not
		// the number of improvements.
		city.AddBuilding(new Building(new SaveBuilding {
			name = "Secret Police",
			flags = { SaveBuilding.Flag.ResistantToBribery },
		}, gd));
		Assert.Equal(-20, Espionage.PropagandaPenalty(gd, actor, target, city));
	}

	[Fact]
	public void PenaltySubtractsTenForWeLoveTheKingDay() {
		(C7GameData.GameData gd, Player actor, Player target, City city, _) = SetupCity();

		Assert.Equal(0, Espionage.PropagandaPenalty(gd, actor, target, city));
		city.isWeLoveTheKingDay = true;
		Assert.Equal(-10, Espionage.PropagandaPenalty(gd, actor, target, city));
	}

	[Fact]
	public void PenaltyAddsTenForCivilDisorder() {
		(C7GameData.GameData gd, Player actor, Player target, City city, _) = SetupCity();

		Assert.Equal(0, Espionage.PropagandaPenalty(gd, actor, target, city));
		city.isInCivilDisorder = true;
		Assert.Equal(10, Espionage.PropagandaPenalty(gd, actor, target, city));
	}

	[Fact]
	public void PenaltyAddsTheGovernmentsModifierAgainstTheTargetsGovernment() {
		(C7GameData.GameData gd, Player actor, Player target, City city, _) = SetupCity();
		List<Government> governments = PropagandaGame.InstallShippedGovernments(gd);

		// Democracy acting against Despotism: the Democracy row, Despotism
		// column of the shipped matrix is +30.
		actor.government = governments[5];
		target.government = governments[1];
		Assert.Equal(30, Espionage.PropagandaPenalty(gd, actor, target, city));

		// The other direction of the same pair is the Despotism row,
		// Democracy column: -30. The pair is asymmetric, so a transposed
		// read fails here.
		actor.government = governments[1];
		target.government = governments[5];
		Assert.Equal(-30, Espionage.PropagandaPenalty(gd, actor, target, city));

		// A second asymmetric pair, and the all-zero Anarchy row.
		actor.government = governments[3];
		target.government = governments[2];
		Assert.Equal(5, Espionage.PropagandaPenalty(gd, actor, target, city));
		actor.government = governments[2];
		target.government = governments[3];
		Assert.Equal(-5, Espionage.PropagandaPenalty(gd, actor, target, city));
		actor.government = governments[0];
		target.government = governments[6];
		Assert.Equal(0, Espionage.PropagandaPenalty(gd, actor, target, city));
	}
}

// The per-citizen roll terms of spec 24 section 6.6 step 2 and the
// cultural-level selection of its closing paragraph.
public class PropagandaRollTest {
	[Fact]
	public void CitizenChanceAddsTwentyForActorNationalsAndClampsAtBothEdges() {
		// The spec's worked example (8.3): level-2 culture (20), no penalty,
		// a foreign citizen rolls 20 and one of ours rolls 40.
		Assert.Equal(20, Espionage.PropagandaCitizenChance(20, 0, false));
		Assert.Equal(40, Espionage.PropagandaCitizenChance(20, 0, true));

		// The clamp's lower edge: a penalty deeper than base + 20 reaches 0,
		// with and without the nationality term.
		Assert.Equal(0, Espionage.PropagandaCitizenChance(3, -40, false));
		Assert.Equal(0, Espionage.PropagandaCitizenChance(3, -40, true));

		// The upper edge: base + D + 20 can exceed 95 in modified rules, and
		// is clamped there.
		Assert.Equal(95, Espionage.PropagandaCitizenChance(30, 60, true));
		Assert.Equal(95, Espionage.PropagandaCitizenChance(30, 70, false));

		// An ordinary unclamped value.
		Assert.Equal(35, Espionage.PropagandaCitizenChance(25, 10, false));
	}

	[Fact]
	public void PropagandaChancePicksTheLargestThresholdNotExceedingTheRatioInAnyRowOrder() {
		// The shipped rows, once descending (how the BIQ stores them) and
		// once ascending: the selection rule is order-independent, so both
		// orders must answer with the same level. Against the pre-change
		// implementation (first row whose threshold the ratio meets) the
		// ascending order fails every assertion.
		foreach (bool descending in new[] { true, false }) {
			List<CultureLevel> levels = EspionageTestGame.CultureLevels();
			if (!descending) {
				levels.Reverse();
			}

			// Ratio 300 selects "in awe of" (30) - the threshold counts when
			// it equals the ratio.
			Assert.Equal(30, ChanceFor(levels, ownCulture: 3000, otherCulture: 1000));
			// Ratio 150 falls between 100 and 200: the largest threshold not
			// exceeding it is 100, i.e. "impressed with" (20).
			Assert.Equal(20, ChanceFor(levels, ownCulture: 1500, otherCulture: 1000));
			// Ratio 33 is exactly "disdainful of" (3)...
			Assert.Equal(3, ChanceFor(levels, ownCulture: 330, otherCulture: 1000));
			// ... and 32 is below every threshold, which falls back to the
			// default (the lowest-threshold) level: also 3.
			Assert.Equal(3, ChanceFor(levels, ownCulture: 320, otherCulture: 1000));
		}
	}

	private static int ChanceFor(List<CultureLevel> levels, int ownCulture, int otherCulture) {
		C7GameData.GameData gd = new(5) { cultureLevels = levels };
		Player actor = CulturePlayer(gd, "actor", ownCulture);
		Player target = CulturePlayer(gd, "target", otherCulture);
		return Espionage.PropagandaChance(gd, actor, target);
	}

	private static Player CulturePlayer(C7GameData.GameData gd, string key, int culture) {
		Player player = new() {
			id = gd.ids.CreateID(key),
			civilization = new Civilization(),
			government = new Government(),
		};
		City city = new(null, player, $"{key} city", gd.ids.CreateID("city"));
		city.perPlayerCulture[player] = culture;
		player.cities.Add(city);
		return player;
	}
}

// Mission 6 end to end through RunMission (spec 24 section 6.6). Against the
// pre-change tree every test here fails: RunMission returned
// "Initiate Propaganda has no engine effect yet" with ran = false, no gold
// was spent, no faces were raised and no city ever changed owner.
public class PropagandaMissionTest {
	private static (C7GameData.GameData gd, Player actor, Player target, City targetCity) SetupCity(int population, int actorNationals) {
		C7GameData.GameData gd = PropagandaGame.NewGameData(out Player actor, out Player target);
		Tile tile = gd.map.tileAt(11, 11);
		City targetCity = PropagandaGame.AddTargetCity(gd, actor, target, tile, population, actorNationals);
		return (gd, actor, target, targetCity);
	}

	private static PlayerRelationship Relationship(Player from, Player to) {
		return from.playerRelationships[to.id];
	}

	[Fact]
	public void SeventyFourPercentSubvertedBoilsTheCityWithoutChangingItsOwner() {
		(C7GameData.GameData gd, Player actor, Player target, City targetCity) = SetupCity(population: 100, actorNationals: 74);
		// Base 30 for the 26 foreign citizens (30 <= 40 fails), 50 for the 74
		// of our nationality (40 < 50 passes): S = 74, and 74 * 100 / 100 = 74
		// is below the 75% threshold.
		C7GameData.GameData.rng = new FixedRandom(40);

		EspionageMissionResult result = Espionage.RunMission(gd, actor, target, targetCity, Espionage.InitiatePropaganda, EspionageAgent.Spy);

		Assert.True(result.ran);
		Assert.False(result.succeeded);
		Assert.False(result.agentCaught);
		Assert.False(string.IsNullOrEmpty(result.message));

		// The city merely boils: ownership is untouched and the counter holds
		// exactly S unhappy faces.
		Assert.Equal(target, targetCity.owner);
		Assert.Contains(targetCity, target.cities);
		Assert.DoesNotContain(targetCity, actor.cities);
		Assert.Equal(74, targetCity.unhappyFacesDueToPropaganda);

		// The cost is paid before the resolution, like every other mission.
		Assert.True(result.costPaid > 0);
		Assert.Equal(100000 - result.costPaid, actor.gold);

		// Propaganda never exposes an agent (spec 6.6, 7): the planted agent
		// survives both branches and no caught-spy counter moves.
		Assert.True(Relationship(actor, target).agentPlanted);
		Assert.Equal(0, Relationship(target, actor).caughtSpyCount);
	}

	[Fact]
	public void SeventyFivePercentSubvertedRevoltsAndCapturesTheCity() {
		(C7GameData.GameData gd, Player actor, Player target, City targetCity) = SetupCity(population: 100, actorNationals: 75);
		// Same roll, one more national citizen: S = 75 and 75 * 100 / 100 = 75
		// reaches the threshold, so the city changes hands.
		C7GameData.GameData.rng = new FixedRandom(40);

		EspionageMissionResult result = Espionage.RunMission(gd, actor, target, targetCity, Espionage.InitiatePropaganda, EspionageAgent.Spy);

		Assert.True(result.ran);
		Assert.True(result.succeeded);
		Assert.False(result.agentCaught);
		Assert.False(string.IsNullOrEmpty(result.message));

		// The flip goes through CityInteractions.CaptureCity: same city
		// object, new owner, moved between the two city lists.
		Assert.Equal(actor, targetCity.owner);
		Assert.Same(targetCity, actor.cities.Single(c => c.name == "Target"));
		Assert.DoesNotContain(targetCity, target.cities);
		Assert.Equal(75, targetCity.unhappyFacesDueToPropaganda);

		// Still never caught.
		Assert.Equal(0, Relationship(target, actor).caughtSpyCount);
	}

	[Fact]
	public void ThePropagandaFacesAreRaisedToTheMaximumOfOldAndSubverted() {
		// Old counter above S: the boil must not lower it. S = 2 here (the two
		// nationals pass the roll of 40, the two foreign citizens fail), and
		// 2 * 100 / 4 = 50 boils the city.
		(C7GameData.GameData gd, Player actor, Player target, City targetCity) = SetupCity(population: 4, actorNationals: 2);
		targetCity.unhappyFacesDueToPropaganda = 5;
		C7GameData.GameData.rng = new FixedRandom(40);

		EspionageMissionResult result = Espionage.RunMission(gd, actor, target, targetCity, Espionage.InitiatePropaganda, EspionageAgent.Spy);

		Assert.False(result.succeeded);
		Assert.Equal(target, targetCity.owner);
		Assert.Equal(5, targetCity.unhappyFacesDueToPropaganda);

		// Old counter below S: the boil raises it to S, not past it.
		(C7GameData.GameData gd2, Player actor2, Player target2, City city2) = SetupCity(population: 4, actorNationals: 2);
		city2.unhappyFacesDueToPropaganda = 1;
		C7GameData.GameData.rng = new FixedRandom(40);

		EspionageMissionResult second = Espionage.RunMission(gd2, actor2, target2, city2, Espionage.InitiatePropaganda, EspionageAgent.Spy);

		Assert.False(second.succeeded);
		Assert.Equal(2, city2.unhappyFacesDueToPropaganda);
	}

	[Fact]
	public void TheMissionSkipsTheGenericSingleRoll() {
		(C7GameData.GameData gd, Player actor, Player target, City targetCity) = SetupCity(population: 4, actorNationals: 4);
		// The first draw is 99. Under a generic odds roll (99 >= 30) the whole
		// mission would fail right there; spec 6.6 has no such roll - the
		// draw only fails the first citizen, the other three pass, and
		// 3 * 100 / 4 = 75 flips the city. The rest of the sequence only feeds
		// the capture path's resistance rolls, which must all miss.
		int[] sequence = new[] { 99, 0, 0, 0 }.Concat(Enumerable.Repeat(99, 20)).ToArray();
		C7GameData.GameData.rng = new SequenceRandom(sequence);

		EspionageMissionResult result = Espionage.RunMission(gd, actor, target, targetCity, Espionage.InitiatePropaganda, EspionageAgent.Spy);

		Assert.True(result.succeeded);
		Assert.Equal(actor, targetCity.owner);
		Assert.Equal(3, targetCity.unhappyFacesDueToPropaganda);
	}

	[Fact]
	public void BoiledFacesMakeTheCitizensUnhappyOnTheRecompute() {
		(C7GameData.GameData gd, Player actor, Player target, City targetCity) = SetupCity(population: 4, actorNationals: 2);

		// Six born-content citizens: all four citizens start content.
		targetCity.RecalculateCitizenMoods(gd);
		Assert.Equal(4, targetCity.residents.Count(r => r.mood == CityResident.Mood.Content));
		Assert.Equal(0, targetCity.residents.Count(r => r.mood == CityResident.Mood.Unhappy));

		// The boil raises the counter to S = 2 and recomputes, so two content
		// citizens become unhappy (spec 15 section 4.5: the propaganda faces
		// are subtracted from the happy-face total).
		C7GameData.GameData.rng = new FixedRandom(40);
		EspionageMissionResult result = Espionage.RunMission(gd, actor, target, targetCity, Espionage.InitiatePropaganda, EspionageAgent.Spy);

		Assert.False(result.succeeded);
		Assert.Equal(2, targetCity.unhappyFacesDueToPropaganda);
		Assert.Equal(2, targetCity.residents.Count(r => r.mood == CityResident.Mood.Unhappy));
		Assert.Equal(2, targetCity.residents.Count(r => r.mood == CityResident.Mood.Content));
	}
}

// The government-pair propaganda modifier asserted as a complete set against
// the shipped conquests.biq, measured with a QueryCiv3 probe of every pair.
// The first member of the GOVT_GOVT triple reads as the 0xCDCDCDCD fill in
// the shipped file and the third is the (already tested) ResistanceModifier,
// so only the middle-member read can reproduce these values - a wrong-column
// import fails every non-zero cell.
public class PropagandaRulesDataTest {
	[SkippableFact]
	public void ShippedBiq_CarriesTheWholePropagandaModifierMatrix() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);
		List<SaveTech> techs = Enumerable.Range(0, biq.Tech.Length)
			.Select(i => new SaveTech { id = ID.FromString("Tech-" + (i + 1)) })
			.ToList();
		List<Government> governments = ImportCiv3.BuildGovernments(biq, new ID.Factory(), techs);

		Assert.Equal(PropagandaGame.GovernmentNames.Length, governments.Count);
		for (int row = 0; row < governments.Count; row++) {
			Assert.Equal(PropagandaGame.GovernmentNames[row], governments[row].name);
			Assert.Equal(PropagandaGame.GovernmentNames.Length, governments[row].briberyModifiers.Count);
			for (int column = 0; column < PropagandaGame.GovernmentNames.Length; column++) {
				Assert.Equal(PropagandaGame.BriberyModifiers[row, column],
					governments[row].BriberyModifierAgainst(column));
			}
		}

		// The middle member is a different field from the third: spot-check
		// one cell where they disagree in sign so a column slip cannot pass.
		Assert.NotEqual(governments[1].BriberyModifierAgainst(5),
			governments[1].ResistanceModifierAgainst(5));
	}
}
