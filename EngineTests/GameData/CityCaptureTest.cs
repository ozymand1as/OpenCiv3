using System.Collections.Generic;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

/// The city-capture act (spec 17 section 7) and the size-zero razing rule
/// (spec 13 section 5). Everything here drives the engine through APIs that
/// existed before the capture feature, so both load-bearing tests fail
/// against the pre-change tree: a unit entering an enemy city used to destroy
/// the city outright, and a city whose last citizen starved used to sit there
/// empty instead of being razed.
public class CityCaptureTest {

	// ------------------------------------------------------------------ fixture

	internal static class Game {
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

		public static C7GameData.GameData NewGameData(out Player attacker, out Player defender) {
			C7GameData.GameData gd = new(customSeed: 7);
			gd.map = MakeMap(8, 8);
			gd.rules = new Rules() {
				MaxRankOfWorkableTiles = 2,
				MaximumLevel1CitySize = 6,
				MaximumLevel2CitySize = 12,
			};
			gd.gameDifficulty = new Difficulty() { PercentageOfOptimalCities = 100, MilitaryLaw = 1 };
			gd.difficulties = new List<Difficulty> { gd.gameDifficulty };
			gd.citizenTypes = new List<CitizenType> {
				new() { IsDefaultCitizen = true, SingularName = "Laborer" },
				new() { SingularName = "Scientist", Research = 3, Luxuries = 1, Taxes = 2 },
			};

			attacker = NewPlayer(gd, "Player-1", "Attackers");
			defender = NewPlayer(gd, "Player-2", "Defenders");
			// Both relationships exist and carry no deals, which is how the
			// engine reads "at war" (PlayerRelationship.AtWar).
			attacker.playerRelationships[defender.id] = new PlayerRelationship();
			defender.playerRelationships[attacker.id] = new PlayerRelationship();
			gd.players.Add(attacker);
			gd.players.Add(defender);

			// Hold the random rolls still: with no culture levels loaded the
			// capture rolls nothing anyway, but keep any stray roll fixed.
			C7GameData.GameData.rng = new FixedRandom(99);

			EngineStorage.InitializeGameDataForTests(gd);
			return gd;
		}

		private static Player NewPlayer(C7GameData.GameData gd, string id, string civ) {
			return new Player() {
				id = ID.FromString(id),
				civilization = new Civilization(civ),
				government = new Government(),
				rules = gd.rules,
			};
		}

		public static GameMap MakeMap(int width, int height) {
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

		public static City AddCity(C7GameData.GameData gd, Player owner, Tile tile, int population, int cultureForOwner = 0) {
			City city = new City(tile, owner, $"{owner.civilization.name} City {gd.cities.Count + 1}", gd.ids.CreateID("city"));
			for (int i = 0; i < population; ++i) {
				CityResident resident = new CityResident() {
					citizenType = gd.citizenTypes.Find(c => c.IsDefaultCitizen),
					nationality = owner.civilization,
					city = city,
				};
				city.residents.Add(resident);
			}
			city.perPlayerCulture[owner] = cultureForOwner;
			tile.cityAtTile = city;
			owner.cities.Add(city);
			gd.cities.Add(city);
			return city;
		}

		public static MapUnit PlaceUnit(C7GameData.GameData gd, Player owner, Tile tile, int attack = 1) {
			MapUnit unit = new MapUnit(gd.ids.CreateID("unit")) {
				name = "Warrior",
				unitType = new UnitPrototype() { name = "Warrior", attack = attack, defense = 1 },
				owner = owner,
				nationality = owner.civilization,
			};
			unit.unitType.categories.Add("Land");
			unit.location = tile;
			tile.unitsOnTile.Add(unit);
			owner.units.Add(unit);
			return unit;
		}
	}

	// ------------------------------------------------------------------- tests

	[Fact]
	public void UnitEnteringEnemyCity_TransfersOwnershipAndKeepsPopulationAndCulture() {
		C7GameData.GameData gd = Game.NewGameData(out Player attacker, out Player defender);
		Tile tile = gd.map.tileAt(4, 4);
		City city = Game.AddCity(gd, defender, tile, population: 3, cultureForOwner: 150);
		MapUnit unit = Game.PlaceUnit(gd, attacker, tile);

		unit.OnEnterTile(tile);

		// The city survives the transfer as the same city object.
		Assert.Same(city, tile.cityAtTile);
		Assert.Equal(attacker, city.owner);
		Assert.Contains(city, attacker.cities);
		Assert.DoesNotContain(city, defender.cities);

		// Population survives: no citizen is removed by the capture.
		Assert.Equal(3, city.residents.Count);

		// Culture survives (spec 17 section 7: nothing clears a captured
		// city's per-player culture), and the new owner's own slot starts at
		// zero rather than overwriting anything.
		Assert.Equal(150, city.perPlayerCulture[defender]);
		Assert.True(city.perPlayerCulture.ContainsKey(attacker));
		Assert.Equal(0, city.perPlayerCulture[attacker]);
	}

	[Fact]
	public void StarvingTheLastCitizen_RazesTheCity() {
		C7GameData.GameData gd = Game.NewGameData(out _, out Player defender);
		Tile tile = gd.map.tileAt(4, 4);
		City city = Game.AddCity(gd, defender, tile, population: 1);
		city.foodStored = -1000;

		city.HandleCityGrowth(gd);

		// A city whose size reaches 0 is razed (spec 13 section 5): the city
		// is gone from the tile and from both city lists.
		Assert.Null(tile.cityAtTile);
		Assert.DoesNotContain(city, gd.cities);
		Assert.DoesNotContain(city, defender.cities);
	}
}
