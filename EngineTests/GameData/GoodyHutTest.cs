using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Covers the goody-hut tile feature end to end: the save format, consumption
/// when a unit enters the tile, and the per-outcome rules.
/// </summary>
public class GoodyHutTest {
	private static TerrainType Grass() {
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

	private static TerrainType Coast() {
		return new TerrainType() { Key = "coast", movementCost = 1, allowCities = false, height = 1 };
	}

	/// <summary>A square map of land tiles with neighbours wired up.</summary>
	private static GameMap MakeMap(int width = 8, int height = 8) {
		TerrainType grass = Grass();
		GameMap map = new GameMap() { numTilesWide = width, numTilesTall = height, tiles = new List<Tile>() };

		for (int i = 0; i < width * height; ++i) {
			map.tileIndexToCoords(i, out int x, out int y);
			map.tiles.Add(new Tile(ID.None("tile")) {
				XCoordinate = x,
				YCoordinate = y,
				continent = 0,
				baseTerrainType = grass,
				overlayTerrainType = grass,
			});
		}

		map.computeNeighbors();
		return map;
	}

	private static C7GameData.GameData MakeGameData(out Player player, int mapSize = 8) {
		C7GameData.GameData gd = new(customSeed: 7);
		gd.map = MakeMap(mapSize, mapSize);
		gd.rules = new Rules() { MaxRankOfWorkableTiles = 2 };
		gd.difficulties = new List<Difficulty> { new(), new(), new(), new() };
		gd.gameDifficulty = gd.difficulties[3];
		gd.barbarianInfo = new BarbarianInfo();
		gd.experienceLevels = new List<ExperienceLevel> { new("veteran", "Veteran", 2, 0, 0) };
		gd.defaultExperienceLevel = gd.experienceLevels[0];
		gd.citizenTypes = new List<CitizenType> { new() { IsDefaultCitizen = true } };

		player = new Player() {
			civilization = new Civilization("Test"),
			government = new Government(),
		};
		// A city-name list is needed by the City outcome, which asks for the next
		// unused name when it founds its city.
		player.civilization.cityNames.Add("Test City");
		player.rules = gd.rules;
		gd.players.Add(player);

		EngineStorage.InitializeGameDataForTests(gd);
		return gd;
	}

	private static MapUnit PlaceUnit(Player player, Tile tile) {
		MapUnit unit = new(ID.None("unit")) {
			name = "Test unit",
			unitType = new UnitPrototype() { name = "Warrior", attack = 1, defense = 1 },
			owner = player,
		};
		unit.unitType.categories.Add("Land");
		unit.location = tile;
		tile.unitsOnTile.Add(unit);
		player.units.Add(unit);
		return unit;
	}

	private static C7GameData.GameData SetupHut(out Player player, out Tile tile) {
		C7GameData.GameData gd = MakeGameData(out player);
		tile = gd.map.tileAt(4, 4);
		tile.hasGoodyHut = true;
		return gd;
	}

	/// <summary>
	/// A second living civilization and a city the player already owns. The
	/// City and Settlers outcomes divide the world's cities by livingPlayers-1
	/// and require the player to own a city already.
	/// </summary>
	private static void AddRivalAndAnExistingCity(C7GameData.GameData gd, Player player) {
		Player rival = new() {
			civilization = new Civilization("Rival"),
			government = new Government(),
		};
		rival.civilization.cityNames.Add("Rival City");
		rival.rules = gd.rules;
		gd.players.Add(rival);

		Tile oldTile = gd.map.tileAt(1, 1);
		City oldCity = new(oldTile, player, "Old city", gd.ids.CreateID("city"));
		oldTile.cityAtTile = oldCity;
		player.cities.Add(oldCity);
		gd.cities.Add(oldCity);
	}

	private static C7GameData.GameData SetupCityHut(out Player player, out Tile tile) {
		C7GameData.GameData gd = SetupHut(out player, out tile);
		player.civilization.traits.Add(Civilization.Trait.Expansionist);
		AddRivalAndAnExistingCity(gd, player);
		return gd;
	}

	private static C7GameData.GameData SetupBarbarianHut(out Player player, out Tile tile, out Player barbarians) {
		C7GameData.GameData gd = SetupHut(out player, out tile);
		barbarians = new Player() {
			civilization = new Civilization("Barbarians") { isBarbarian = true },
			government = new Government(),
		};
		barbarians.civilization.cityNames.Add("Barbarian City");
		barbarians.rules = gd.rules;
		gd.players.Add(barbarians);

		gd.barbarianInfo.basicBarbarian = new UnitPrototype() { name = "Warrior" };
		gd.barbarianInfo.basicBarbarian.categories.Add("Land");

		player.cities.Add(new City(tile, player, "Test", ID.None("city")));
		PlaceUnit(player, tile);
		return gd;
	}

	private static UnitPrototype MercenaryCandidate(string name, Civilization civilization) {
		UnitPrototype prototype = new() { name = name };
		prototype.categories.Add("Land");
		prototype.producibleBy.Add(civilization);
		return prototype;
	}

	[Fact]
	public void GoodyHutRoundTripsThroughTheSaveFormat() {
		TerrainType grass = Grass();
		Tile tile = new(ID.None("tile")) {
			XCoordinate = 2,
			YCoordinate = 4,
			baseTerrainType = grass,
			overlayTerrainType = grass,
			hasGoodyHut = true,
		};

		SaveTile saved = new(tile);
		Assert.Contains("goodyHut", saved.features);

		Tile restored = saved.ToTile(new List<TerrainType> { grass }, new List<Resource>(), new List<TerrainImprovement>());
		Assert.True(restored.hasGoodyHut);
	}

	[Fact]
	public void ATileWithoutAHutDoesNotSaveTheFeature() {
		TerrainType grass = Grass();
		Tile tile = new(ID.None("tile")) {
			XCoordinate = 2,
			YCoordinate = 4,
			baseTerrainType = grass,
			overlayTerrainType = grass,
		};

		SaveTile saved = new(tile);
		Assert.DoesNotContain("goodyHut", saved.features);
		Assert.False(saved.ToTile(new List<TerrainType> { grass }, new List<Resource>(), new List<TerrainImprovement>()).hasGoodyHut);
	}

	[Fact]
	public void EnteringAHutTileConsumesIt() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		MapUnit unit = PlaceUnit(player, tile);

		C7GameData.GameData.rng = new Random(4242);

		unit.OnEnterTile(tile);

		Assert.False(tile.hasGoodyHut);
	}

	[Fact]
	public void BarbarianUnitsDoNotConsumeHuts() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.civilization.isBarbarian = true;
		MapUnit unit = PlaceUnit(player, tile);

		unit.OnEnterTile(tile);

		Assert.True(tile.hasGoodyHut);
	}

	[Fact]
	public void AHutAlwaysPopsExactlyOnce() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		MapUnit unit = PlaceUnit(player, tile);

		unit.OnEnterTile(tile);
		Assert.False(tile.hasGoodyHut);

		// A second visit cannot pop it again, and so draws nothing at all.
		C7GameData.GameData.rng = new Random(11);
		Random probe = new(11);
		unit.OnEnterTile(tile);
		Assert.Equal(probe.Next(), C7GameData.GameData.rng.Next());
	}

	[Fact]
	public void TheDifficultyRowComesFromTheGameDifficulty() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);

		Assert.Equal(4, GoodyHutInteractions.RowIndexFor(gd, player));

		player.civilization.traits.Add(Civilization.Trait.Expansionist);
		Assert.Equal(3, GoodyHutInteractions.RowIndexFor(gd, player));
	}

	/// <summary>
	/// The shipped conquests.biq has eight difficulty levels - Chieftain,
	/// Warlord, Regent, Monarch, Emperor, Demigod, Deity, Sid, as read out of
	/// the BIQ with QueryCiv3 - not the seven spec section 4.3 lists, so the
	/// table's last row is reachable in a normal game.
	/// </summary>
	[Fact]
	public void TheShippedEightDifficultyLevelsReachTheLastTableRow() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		gd.difficulties = new List<Difficulty> { new(), new(), new(), new(), new(), new(), new(), new() };

		// Sid, non-Expansionist: row 8, the last row of the table.
		gd.gameDifficulty = gd.difficulties[7];
		Assert.Equal(8, GoodyHutInteractions.RowIndexFor(gd, player));

		// Sid, Expansionist: one row better.
		player.civilization.traits.Add(Civilization.Trait.Expansionist);
		Assert.Equal(7, GoodyHutInteractions.RowIndexFor(gd, player));

		// Demigod, non-Expansionist: row 6.
		player.civilization.traits.Clear();
		gd.gameDifficulty = gd.difficulties[5];
		Assert.Equal(6, GoodyHutInteractions.RowIndexFor(gd, player));
	}

	/// <summary>
	/// A drawn outcome whose preconditions fail must be re-rolled, not turned
	/// into a desert village. Here only the Maps outcome can take effect: the
	/// City toggle is off, the player is in the Middle Ages, the tile carries a
	/// resource, and there is no settler type, no mercenary type and no
	/// barbarian player. A single-roll implementation returns Nothing for
	/// almost every first draw; the re-roll must reach Maps.
	/// </summary>
	[Fact]
	public void AFailedGuardReRollsUntilAnOutcomeApplies() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		gd.gameDifficulty = gd.difficulties[2];
		gd.rules.AllowCitiesFromGoodyHuts = false;
		player.eraCivilopediaName = EraUtils.MIDDLE_AGES_CVLPD;
		tile.Resource = new Resource { Key = "Gold", Category = ResourceCategory.LUXURY };

		C7GameData.GameData.rng = new Random(1);

		Assert.Equal(GoodyHutOutcome.Maps, GoodyHutInteractions.Consume(gd, player, tile));
		Assert.False(tile.hasGoodyHut);
	}

	[Theory]
	[InlineData(1, 25)]
	[InlineData(49, 25)]
	[InlineData(50, 50)]
	[InlineData(200, 50)]
	public void MoneyGivesTwentyFiveGoldBeforeTurnFiftyAndFiftyAfter(int turn, int expectedGold) {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		gd.turn = turn;

		Assert.Equal(GoodyHutOutcome.Money, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Money));
		Assert.Equal(expectedGold, player.gold);
	}

	[Fact]
	public void MoneyIsNothingOnAResourceTile() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		tile.Resource = new Resource { Key = "Gold", Category = ResourceCategory.LUXURY };

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Money));
		Assert.Equal(0, player.gold);
	}

	[Fact]
	public void CityIsNothingForANonExpansionistCiv() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.cities.Add(new City(tile, player, "Test", ID.None("city")));

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.City));
		Assert.False(tile.HasCity());
	}

	[Fact]
	public void CityIsNothingWhenTheRuleToggleIsOff() {
		C7GameData.GameData gd = SetupCityHut(out Player player, out Tile tile);
		gd.rules.AllowCitiesFromGoodyHuts = false;

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.City));
		Assert.False(tile.HasCity());
	}

	/// <summary>
	/// Before turn 50 the population roll does not exist at all: the new city
	/// keeps the single resident the founding call gives it, and no draw is
	/// consumed for population.
	/// </summary>
	[Fact]
	public void CityGainsNoExtraPopulationBeforeTurnFifty() {
		for (int seed = 1; seed <= 20; ++seed) {
			C7GameData.GameData gd = SetupCityHut(out Player player, out Tile tile);
			gd.turn = 49;
			C7GameData.GameData.rng = new Random(seed);

			Assert.Equal(GoodyHutOutcome.City, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.City));
			Assert.True(tile.HasCity());
			Assert.Equal(1, tile.cityAtTile.residents.Count);
		}
	}

	/// <summary>
	/// From turn 50 the new city receives rand_int(4) extra population, so its
	/// size is 1..4 and every size occurs.
	/// </summary>
	[Fact]
	public void CityGainsRandInt4PopulationFromTurnFifty() {
		int[] sizes = new int[6];
		for (int seed = 1; seed <= 120; ++seed) {
			C7GameData.GameData gd = SetupCityHut(out Player player, out Tile tile);
			gd.turn = 60;
			C7GameData.GameData.rng = new Random(seed);

			Assert.Equal(GoodyHutOutcome.City, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.City));
			sizes[tile.cityAtTile.residents.Count]++;
		}

		Assert.Equal(0, sizes[0]);
		Assert.Equal(0, sizes[5]);
		for (int size = 1; size <= 4; ++size) {
			Assert.True(sizes[size] > 10, $"size {size} occurred only {sizes[size]} times");
		}
	}

	/// <summary>
	/// The population roll itself: nothing before turn 50, and from turn 50 on
	/// exactly one uniform draw of four values.
	/// </summary>
	[Fact]
	public void TheHutPopulationRollIsGatedOnTurnFiftyAndSpansFourValues() {
		C7GameData.GameData.rng = new Random(1234);
		Random probe = new(1234);

		Assert.Equal(0, GoodyHutInteractions.HutCityPopulationBonus(49));
		Assert.Equal(probe.Next(), C7GameData.GameData.rng.Next());

		foreach (int turn in new[] { 50, 51, 74 }) {
			Assert.Equal(probe.Next(4), GoodyHutInteractions.HutCityPopulationBonus(turn));
		}
	}

	[Fact]
	public void TechIsNothingOutsideTheAncientEra() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.eraCivilopediaName = EraUtils.MIDDLE_AGES_CVLPD;
		gd.techs.Add(new Tech() { id = ID.None("tech"), Name = "Pottery", EraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD, Cost = 2 });

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Tech));
		Assert.Empty(player.knownTechs);
	}

	[Fact]
	public void TechGrantsExactlyOneAvailableAncientAdvance() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.eraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD;
		Tech pottery = new() { id = gd.ids.CreateID("tech"), Name = "Pottery", EraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD, Cost = 2 };
		Tech bronzeWorking = new() { id = gd.ids.CreateID("tech"), Name = "Bronze Working", EraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD, Cost = 4 };
		gd.techs.Add(bronzeWorking);
		gd.techs.Add(pottery);

		C7GameData.GameData.rng = new Random(77);

		Assert.Equal(GoodyHutOutcome.Tech, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Tech));
		Assert.Contains(player.knownTechs.Single(), new[] { pottery.id, bronzeWorking.id });
	}

	/// <summary>
	/// The Tech outcome scores each candidate with a rand_int(100) tiebreak on
	/// top of its metric, one draw per candidate, in ruleset order. Two
	/// equal-cost candidates must therefore both be reachable, and each seed
	/// must match an independent replay of the scoring.
	/// </summary>
	[Fact]
	public void TheTechOutcomeScoresEachCandidateWithARandInt100Draw() {
		HashSet<ID> granted = new();
		for (int seed = 1; seed <= 60; ++seed) {
			C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
			player.eraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD;
			Tech alpha = new() { id = gd.ids.CreateID("tech"), Name = "Alpha", EraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD, Cost = 3 };
			Tech beta = new() { id = gd.ids.CreateID("tech"), Name = "Beta", EraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD, Cost = 3 };
			gd.techs.Add(alpha);
			gd.techs.Add(beta);

			C7GameData.GameData.rng = new Random(seed);
			Random probe = new(seed);
			int alphaScore = alpha.Cost + probe.Next(100);
			int betaScore = beta.Cost + probe.Next(100);
			Tech expected = alphaScore <= betaScore ? alpha : beta;

			Assert.Equal(GoodyHutOutcome.Tech, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Tech));
			Assert.Contains(expected.id, player.knownTechs);
			granted.Add(expected.id);
		}

		// A deterministic cheapest-first pick would always return the same
		// advance; the tiebreak must have decided at least once.
		Assert.Equal(2, granted.Count);
	}

	/// <summary>Builds a chain of prerequisite-linked Ancient advances.</summary>
	private static List<Tech> PrerequisiteChain(C7GameData.GameData gd, int length) {
		List<Tech> chain = new();
		for (int i = 1; i <= length; ++i) {
			Tech tech = new() {
				id = gd.ids.CreateID("tech"),
				Name = $"Tech {i}",
				EraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD,
				Cost = i,
			};
			if (chain.Count > 0) {
				tech.Prerequisites.Add(chain[^1]);
			}
			chain.Add(tech);
			gd.techs.Add(tech);
		}
		return chain;
	}

	[Fact]
	public void PrerequisiteDepthMeasuresThePrerequisiteTree() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		List<Tech> chain = PrerequisiteChain(gd, 6);

		Assert.Equal(0, GoodyHutInteractions.PrerequisiteDepth(chain[0]));
		Assert.Equal(4, GoodyHutInteractions.PrerequisiteDepth(chain[4]));
		Assert.Equal(5, GoodyHutInteractions.PrerequisiteDepth(chain[5]));
	}

	[Fact]
	public void TechAcceptsAnAdvanceFourPrerequisiteLevelsDeep() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.eraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD;
		List<Tech> chain = PrerequisiteChain(gd, 5);

		// The player knows the first four links, so the fifth (four levels
		// deep) is the only candidate.
		foreach (Tech known in chain.Take(4)) {
			player.knownTechs.Add(known.id);
		}
		C7GameData.GameData.rng = new Random(3);

		Assert.Equal(GoodyHutOutcome.Tech, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Tech));
		Assert.Contains(chain[4].id, player.knownTechs);
	}

	[Fact]
	public void TechRejectsAnAdvanceFivePrerequisiteLevelsDeep() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.eraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD;
		List<Tech> chain = PrerequisiteChain(gd, 6);

		foreach (Tech known in chain.Take(5)) {
			player.knownTechs.Add(known.id);
		}
		C7GameData.GameData.rng = new Random(3);

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Tech));
		Assert.DoesNotContain(chain[5].id, player.knownTechs);
	}

	[Fact]
	public void SettlersIsNothingWhenTheCivAlreadyHasASettler() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		UnitPrototype settler = new() { name = "Settler" };
		settler.actions.Add(UnitAction.BuildCity);
		settler.categories.Add("Land");
		gd.unitPrototypes.Add(settler);
		PlaceUnit(player, tile).unitType = settler;

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Settlers));
		Assert.Single(player.units);
	}

	[Fact]
	public void SettlersSpawnsASettlerAtTheHut() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		AddRivalAndAnExistingCity(gd, player);

		UnitPrototype settler = new() { name = "Settler" };
		settler.actions.Add(UnitAction.BuildCity);
		settler.categories.Add("Land");
		gd.unitPrototypes.Add(settler);

		Assert.Equal(GoodyHutOutcome.Settlers, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Settlers));

		MapUnit spawned = Assert.Single(player.units);
		Assert.Equal("Settler", spawned.unitType.name);
		Assert.Same(tile, spawned.location);
	}

	[Fact]
	public void MercenariesSpawnAUnitThePlayerCanField() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		gd.unitPrototypes.Add(MercenaryCandidate("Warrior", player.civilization));
		C7GameData.GameData.rng = new Random(5);

		Assert.Equal(GoodyHutOutcome.Mercenaries, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Mercenaries));

		MapUnit spawned = Assert.Single(player.units);
		Assert.Equal("Warrior", spawned.unitType.name);
		Assert.Same(tile, spawned.location);
	}

	/// <summary>
	/// Civ3 starts its mercenary catalogue walk at a random unit-type index, so
	/// which acceptable type is handed over varies. A deterministic
	/// first-match walk would always return the same one.
	/// </summary>
	[Fact]
	public void TheMercenaryPickerStartsWithARandomCatalogueIndex() {
		HashSet<string> chosen = new();
		for (int seed = 1; seed <= 40; ++seed) {
			C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
			for (int i = 0; i < 3; ++i) {
				gd.unitPrototypes.Add(MercenaryCandidate($"Unit {i}", player.civilization));
			}

			C7GameData.GameData.rng = new Random(seed);
			Random probe = new(seed);
			int start = probe.Next(3);

			Assert.Equal(GoodyHutOutcome.Mercenaries, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Mercenaries));
			Assert.Equal($"Unit {start}", Assert.Single(player.units).unitType.name);
			chosen.Add($"Unit {start}");
		}

		Assert.Equal(3, chosen.Count);
	}

	[Fact]
	public void MercenariesSkipUnproducibleTypes() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		UnitPrototype unproducible = MercenaryCandidate("Leader", player.civilization);
		unproducible.unproducible = true;
		gd.unitPrototypes.Add(unproducible);
		gd.unitPrototypes.Add(MercenaryCandidate("Warrior", player.civilization));

		// Seed 1 starts the walk on the unproducible entry.
		C7GameData.GameData.rng = new Random(1);

		Assert.Equal(GoodyHutOutcome.Mercenaries, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Mercenaries));
		Assert.Equal("Warrior", Assert.Single(player.units).unitType.name);
	}

	[Fact]
	public void BarbariansNeverSpawnMoreThanThreeUnits() {
		int most = 0;
		for (int seed = 1; seed <= 200; ++seed) {
			C7GameData.GameData gd = SetupBarbarianHut(out Player player, out Tile tile, out Player barbarians);
			C7GameData.GameData.rng = new Random(seed);

			Assert.Equal(GoodyHutOutcome.Barbarians, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Barbarians));
			Assert.InRange(barbarians.units.Count, 0, 3);
			most = Math.Max(most, barbarians.units.Count);

			foreach (MapUnit barbarian in barbarians.units) {
				Assert.Contains(barbarian.location, tile.neighbors.Values);
				Assert.True(barbarian.location.IsLand());
			}
		}

		Assert.Equal(3, most);
	}

	/// <summary>
	/// The first successful spawn needs a 3/4 roll. With a single eligible
	/// neighbour the walk makes exactly one roll, so the unit appears exactly
	/// when that roll is below three.
	/// </summary>
	[Fact]
	public void TheFirstBarbarianSpawnSucceedsThreeTimesInFour() {
		int spawnedTotal = 0;
		for (int seed = 1; seed <= 200; ++seed) {
			C7GameData.GameData gd = SetupBarbarianHut(out Player player, out Tile tile, out Player barbarians);

			// Occupy every neighbour but one, so only one tile can spawn from.
			bool keptOne = false;
			foreach (Tile neighbor in tile.neighbors.Values) {
				if (!keptOne) {
					keptOne = true;
					continue;
				}
				PlaceUnit(player, neighbor);
			}

			C7GameData.GameData.rng = new Random(seed);
			Random probe = new(seed);
			bool expected = probe.Next(4) < 3;

			Assert.Equal(GoodyHutOutcome.Barbarians, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Barbarians));
			Assert.Equal(expected ? 1 : 0, barbarians.units.Count);
			spawnedTotal += barbarians.units.Count;
		}

		// Three quarters of two hundred trials; a certainty would be 200.
		Assert.InRange(spawnedTotal, 120, 180);
	}

	/// <summary>
	/// The second and third successful spawns need a 2/3 and a 1/2 roll. The
	/// walk is replayed here from the spec: the same rotated order, one draw
	/// per eligible tile, with the odds chosen by how many units have already
	/// appeared.
	/// </summary>
	[Fact]
	public void BarbarianSpawnChancesFallToTwoThirdsThenOneHalf() {
		int notThree = 0;
		for (int seed = 1; seed <= 200; ++seed) {
			C7GameData.GameData gd = SetupBarbarianHut(out Player player, out Tile tile, out Player barbarians);
			C7GameData.GameData.rng = new Random(seed);
			Random probe = new(seed);

			List<Tile> expected = new();
			int expectedCount = 0;
			for (int k = 1; k <= 8 && expectedCount < 3; ++k) {
				TileDirection direction = TileDirectionExtensions.All[(gd.turn + k) % TileDirectionExtensions.All.Length];
				Tile neighbor = tile.neighbors[direction];
				if (neighbor == null || neighbor == Tile.NONE || neighbor.IsWater() || neighbor.unitsOnTile.Count > 0) {
					continue;
				}

				bool spawn = expectedCount switch {
					0 => probe.Next(4) < 3,
					1 => probe.Next(3) < 2,
					_ => probe.Next(2) < 1,
				};
				if (!spawn) {
					continue;
				}
				expected.Add(neighbor);
				++expectedCount;
			}

			Assert.Equal(GoodyHutOutcome.Barbarians, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Barbarians));
			Assert.Equal(expectedCount, barbarians.units.Count);
			Assert.Equal(
				expected.Select(t => (t.XCoordinate, t.YCoordinate)).OrderBy(t => t),
				barbarians.units.Select(u => (u.location.XCoordinate, u.location.YCoordinate)).OrderBy(t => t));
			if (expectedCount < 3) {
				++notThree;
			}
		}

		// The declining odds must bite: a deterministic first-three-neighbours
		// spawn would never fall short of three.
		Assert.True(notThree > 0, "no trial spawned fewer than three barbarians");
	}

	/// <summary>
	/// The guards are the outcome's precondition; the chance rolls are its
	/// effect. When every roll fails the outcome still took effect, so it must
	/// not be re-rolled and nothing is reported.
	/// </summary>
	[Fact]
	public void BarbariansTakeEffectEvenWhenEveryChanceRollFails() {
		C7GameData.GameData gd = SetupBarbarianHut(out Player player, out Tile tile, out Player barbarians);

		// Every neighbouring land tile is occupied, so no unit can appear.
		foreach (Tile neighbor in tile.neighbors.Values) {
			if (!neighbor.IsWater()) {
				PlaceUnit(player, neighbor);
			}
		}

		C7GameData.GameData.rng = new Random(9);

		Assert.Equal(GoodyHutOutcome.Barbarians, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Barbarians));
		Assert.Empty(barbarians.units);
	}

	[Fact]
	public void BarbariansNeverComeFromAnExpansionistsHut() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.civilization.traits.Add(Civilization.Trait.Expansionist);
		player.cities.Add(new City(tile, player, "Test", ID.None("city")));
		PlaceUnit(player, tile);

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Barbarians));
	}

	/// <summary>A hut in the middle of a 24x24 land map, so the whole 9x9
	/// neighbourhood fits without wrapping.</summary>
	private static C7GameData.GameData SetupMapsHut(out Player player, out Tile tile) {
		C7GameData.GameData gd = MakeGameData(out player, mapSize: 24);
		tile = gd.map.tileAt(12, 12);
		tile.hasGoodyHut = true;
		return gd;
	}

	/// <summary>The ring number of a tile around the hut, in the enumerator's
	/// own metric: one neighbour step is a delta of two along one axis or one
	/// along both.</summary>
	private static int RingAround(Tile centre, int x, int y) {
		return (Math.Abs(x - centre.XCoordinate) + Math.Abs(y - centre.YCoordinate)) / 2;
	}

	/// <summary>
	/// The Maps window is the complete three-ring block (48 tiles) plus 28 of
	/// the 32 four-ring tiles. The expectation is built here from coordinate
	/// arithmetic rather than from the enumerator the production code uses, so
	/// a wrong window bound cannot hide. The union over many pops reveals every
	/// tile the window covers.
	/// </summary>
	[Fact]
	public void MapsRevealSeventySixTilesAroundTheHut() {
		HashSet<(int, int)> revealed = new();
		Tile centre = null;
		for (int seed = 1; seed <= 60; ++seed) {
			C7GameData.GameData gd = SetupMapsHut(out Player player, out Tile tile);
			centre = tile;
			C7GameData.GameData.rng = new Random(seed);

			Assert.Equal(GoodyHutOutcome.Maps, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Maps));
			foreach (Tile t in gd.map.tiles.Where(t => player.tileKnowledge.isTileKnown(t))) {
				revealed.Add((t.XCoordinate, t.YCoordinate));
			}
		}

		// Every tile in the three innermost rings is inside the window.
		int innerRings = 0;
		for (int dy = -6; dy <= 6; ++dy) {
			for (int dx = -6; dx <= 6; ++dx) {
				if (dx == 0 && dy == 0) {
					continue;
				}
				int ring = (Math.Abs(dx) + Math.Abs(dy)) / 2;
				if ((Math.Abs(dx) + Math.Abs(dy)) % 2 != 0 || ring > 3) {
					continue;
				}
				++innerRings;
				Assert.Contains((centre.XCoordinate + dx, centre.YCoordinate + dy), revealed);
			}
		}
		Assert.Equal(48, innerRings);

		// Of the 32 four-ring tiles the enumerator walks only 28, and it never
		// leaves the four-ring block.
		int outerRing = revealed.Count(p => RingAround(centre, p.Item1, p.Item2) == 4);
		Assert.Equal(28, outerRing);
		Assert.Equal(76, revealed.Count);
		Assert.DoesNotContain(revealed, p => RingAround(centre, p.Item1, p.Item2) > 4);
	}

	/// <summary>
	/// Each tile of the window is revealed with probability 3/4, so a pop
	/// reveals about three quarters of it rather than all of it. Several
	/// independent pops give a stable aggregate.
	/// </summary>
	[Fact]
	public void MapsRevealEachTileWithProbabilityThreeQuarters() {
		int trials = 40;
		int total = 0;
		for (int seed = 1; seed <= trials; ++seed) {
			C7GameData.GameData gd = SetupMapsHut(out Player player, out Tile tile);
			C7GameData.GameData.rng = new Random(seed);

			Assert.Equal(GoodyHutOutcome.Maps, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Maps));
			int revealed = gd.map.tiles.Count(t => player.tileKnowledge.isTileKnown(t));
			Assert.InRange(revealed, 0, 76);
			total += revealed;
		}

		// 40 pops of 76 tiles, each revealed with probability 3/4, is 2280
		// expected; revealing everything would be 3040.
		Assert.InRange(total, (int)(76 * trials * 0.70), (int)(76 * trials * 0.80));
	}

	/// <summary>
	/// A tile on another continent is only revealed when it is coast.
	/// </summary>
	[Fact]
	public void MapsSkipForeignTilesUnlessTheyAreCoast() {
		bool coastRevealed = false;
		for (int seed = 1; seed <= 60; ++seed) {
			C7GameData.GameData gd = SetupMapsHut(out Player player, out Tile tile);

			Tile foreignLand = gd.map.tileAt(13, 13);
			foreignLand.continent = 1;

			Tile foreignCoast = gd.map.tileAt(13, 11);
			TerrainType coast = Coast();
			foreignCoast.baseTerrainType = coast;
			foreignCoast.overlayTerrainType = coast;
			foreignCoast.continent = 1;

			C7GameData.GameData.rng = new Random(seed);
			Assert.Equal(GoodyHutOutcome.Maps, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Maps));

			Assert.False(player.tileKnowledge.isTileKnown(foreignLand), "foreign land must stay hidden");
			coastRevealed |= player.tileKnowledge.isTileKnown(foreignCoast);
		}

		Assert.True(coastRevealed, "a foreign coast tile is revealed like any other");
	}

	[Fact]
	public void MapsSetTheGlobalRevealedFlag() {
		C7GameData.GameData gd = SetupMapsHut(out Player player, out Tile tile);
		Assert.False(gd.mapHasBeenRevealed);

		C7GameData.GameData.rng = new Random(3);

		Assert.Equal(GoodyHutOutcome.Maps, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Maps));
		Assert.True(gd.mapHasBeenRevealed);
	}
}
