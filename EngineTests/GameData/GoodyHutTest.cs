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
/// when a unit enters the tile, and the per-outcome guards.
/// </summary>
public class GoodyHutTest {
	private static TerrainType Grass() {
		return new TerrainType() { Key = "grassland", movementCost = 1, allowCities = true, height = 1 };
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

	private static C7GameData.GameData MakeGameData(out Player player) {
		C7GameData.GameData gd = new(customSeed: 7);
		gd.map = MakeMap();
		gd.rules = new Rules();
		gd.difficulties = new List<Difficulty> { new(), new(), new(), new() };
		gd.gameDifficulty = gd.difficulties[3];
		gd.barbarianInfo = new BarbarianInfo();
		gd.experienceLevels = new List<ExperienceLevel> { new("veteran", "Veteran", 2, 0, 0) };
		gd.defaultExperienceLevel = gd.experienceLevels[0];

		player = new Player() {
			civilization = new Civilization("Test"),
			government = new Government(),
		};
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
	public void EnteringAHutTileConsumesItAndMakesExactlyOneDraw() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		MapUnit unit = PlaceUnit(player, tile);

		C7GameData.GameData.rng = new Random(4242);
		Random probe = new(4242);

		unit.OnEnterTile(tile);

		Assert.False(tile.hasGoodyHut);
		probe.Next(20);
		Assert.Equal(probe.Next(), C7GameData.GameData.rng.Next());
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

		// A second visit cannot pop it again.
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
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.civilization.traits.Add(Civilization.Trait.Expansionist);
		player.cities.Add(new City(tile, player, "Test", ID.None("city")));
		gd.rules.AllowCitiesFromGoodyHuts = false;

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.City));
		Assert.False(tile.HasCity());
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
	public void TechGrantsAnAvailableAncientAdvance() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.eraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD;
		Tech pottery = new() { id = gd.ids.CreateID("tech"), Name = "Pottery", EraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD, Cost = 2 };
		Tech bronzeWorking = new() { id = gd.ids.CreateID("tech"), Name = "Bronze Working", EraCivilopediaName = EraUtils.ANCIENT_TIMES_CVLPD, Cost = 4 };
		gd.techs.Add(bronzeWorking);
		gd.techs.Add(pottery);

		Assert.Equal(GoodyHutOutcome.Tech, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Tech));
		Assert.Contains(pottery.id, player.knownTechs);
		Assert.DoesNotContain(bronzeWorking.id, player.knownTechs);
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
	public void BarbariansSpawnUpToThreeUnitsOnNeighbouringLand() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		Player barbarians = new() {
			civilization = new Civilization("Barbarians") { isBarbarian = true },
		};
		gd.players.Add(barbarians);
		gd.barbarianInfo.basicBarbarian = new UnitPrototype() { name = "Warrior" };
		gd.barbarianInfo.basicBarbarian.categories.Add("Land");

		player.cities.Add(new City(tile, player, "Test", ID.None("city")));
		PlaceUnit(player, tile);

		C7GameData.GameData.rng = new Random(99);
		Random probe = new(99);

		Assert.Equal(GoodyHutOutcome.Barbarians, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Barbarians));
		Assert.Equal(3, barbarians.units.Count);
		foreach (MapUnit barbarian in barbarians.units) {
			Assert.Contains(barbarian.location, tile.neighbors.Values);
			Assert.True(barbarian.location.IsLand());
		}

		// Applying an outcome draws nothing; the draw belongs to the roll.
		Assert.Equal(probe.Next(), C7GameData.GameData.rng.Next());
	}

	[Fact]
	public void BarbariansNeverComeFromAnExpansionistsHut() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);
		player.civilization.traits.Add(Civilization.Trait.Expansionist);
		player.cities.Add(new City(tile, player, "Test", ID.None("city")));
		PlaceUnit(player, tile);

		Assert.Equal(GoodyHutOutcome.Nothing, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Barbarians));
	}

	[Fact]
	public void MapsRevealTheHutNeighbourhood() {
		C7GameData.GameData gd = SetupHut(out Player player, out Tile tile);

		Assert.Equal(GoodyHutOutcome.Maps, GoodyHutInteractions.Apply(gd, player, tile, GoodyHutOutcome.Maps));

		// The enumerator the maps outcome walks starts after the centre tile.
		foreach (Tile t in tile.GetTilesWithinTileSquare(3).Where(t => t != tile && t != Tile.NONE)) {
			Assert.True(player.tileKnowledge.isTileKnown(t), $"{t} should be known");
		}
	}
}
