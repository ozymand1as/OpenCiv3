using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using C7Engine;
using C7Engine.Lua;
using C7Engine.Pathing;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the city-tile half of the road and railroad movement rules
/// (11_movement.md sections 3.2, 3.3 and 4.2).
///
/// In the original, Tile_Check_Roads (0x5d9ff0) and Tile_Check_Railroads
/// (0x5da0d0) answer for a city or colony tile only when its owner knows the
/// technology held in the rules fields at +0x1A4 (roads) and +0x218
/// (railroads), which are the Required technology of the Road and Railroad
/// terrain-improvement records. Leader_has_tech (0x561440) answers true for a
/// tech id of -1, so a rules set that leaves a field at -1 disables that half
/// of the gate and the raw overlay bit decides.
///
/// Measured in the shipped conquests.biq the road field is -1 and the railroad
/// field is 44 (Steam Power): the shipped road gate is INERT and the shipped
/// railroad gate is LIVE. That is why the road half of this suite has to use a
/// synthetic rules object - with the shipped values no test can distinguish
/// "the road gate passed" from "there is no road gate" - while the railroad
/// half can use the shipped shape of the values.
///
/// Founding a city also sets the tile's road flag, and sets its railroad flag
/// when the owner knows the rules' railroad technology (0x4ae2a0: the
/// Tile_get_road_bonus test at 0x4ae651, the zero test at 0x4ae656, the jump
/// over the write at 0x4ae658, the +0x218 read at 0x4ae66b and Set_Tile_Flags
/// at 0x4ae6e1). The terrain's road bonus gates that whole write: a terrain
/// with RoadsBonus 0 gets no road and no railroad, which the shipped rules
/// cannot reach (every terrain they allow a city on carries 1) but a rules set
/// can.
/// </summary>
public sealed class CityTileRoadGateTest : MapBase {
	private const double Tolerance = 0.0001;
	private const double RoadCost = 1.0 / 3.0;

	// The shipped rules' railroad technology, Steam Power. The BIQ stores it as
	// TFRM[4].Required = 44 and the fork's tech ids are 1-based, so the id is
	// ruleset.json's "tech-45".
	private static readonly ID RailTech = ID.FromString("tech-45");

	// A synthetic road technology for the cases the shipped data cannot reach:
	// the shipped road field is -1, so the shipped road gate never fires.
	private static readonly ID RoadTech = ID.FromString("tech-999");

	public CityTileRoadGateTest() {
		// Adding a road invalidates the cached trade network, which needs game data.
		InitializeTestGameData();
	}

	// A player with the shipped shape of the gate: no road technology (the
	// shipped field is -1) and the railroad technology set.
	private static Player MakePlayer(params ID[] knownTechs) {
		Player player = new() {
			isHuman = false,
			rules = new Rules { CityRailroadRequiredTech = RailTech },
		};
		foreach (ID tech in knownTechs) {
			player.knownTechs.Add(tech);
		}
		return player;
	}

	private Tile AddRoadedDestination() {
		Tile destination = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		destination.overlays.Add(road);
		return destination;
	}

	// ------------------------------------------------------------------
	// The railroad gate, using the shipped shape of the values.
	// ------------------------------------------------------------------

	[Fact]
	private void ACityTilesRailroadNeedsTheOwnersTechnology() {
		Player player = MakePlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		startTile.overlays.Add(railroad);

		Tile destination = AddRoadedDestination();
		destination.overlays.Add(railroad);

		// The city tile's owner does not know Steam Power, so the railroad on
		// the city tile does not participate. The railroad also sets the road
		// bit, so the tile still participates as a road: the step costs the
		// road cost, not nothing.
		Assert.Equal(RoadCost,
			TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void ACityTilesRailroadIsFreeOnceTheOwnerHasTheTechnology() {
		Player player = MakePlayer(RailTech);
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		startTile.overlays.Add(railroad);

		Tile destination = AddRoadedDestination();
		destination.overlays.Add(railroad);

		Assert.Equal(0.0,
			TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void AFoundingCallbackKeepsARailroadTheOwnerCannotUseYet() {
		// The imported-city case: the tile already carries a railroad, and the
		// city arrives while its owner is still short of the technology. The
		// raw bit has to survive the founding callback, because the gate - not
		// the overlay - decides participation, and the bit has to be there once
		// the technology arrives.
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.terrainImprovements.Add(railroad);

		Player player = MakePlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(railroad);
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));

		Assert.False(startTile.HasRailroad());

		player.knownTechs.Add(RailTech);
		Assert.True(startTile.HasRailroad());
	}

	// ------------------------------------------------------------------
	// The road gate, with a synthetic rules object because the shipped
	// road technology is -1.
	// ------------------------------------------------------------------

	[Fact]
	private void ACityTilesRoadNeedsTheOwnersSyntheticTechnology() {
		EngineStorage.gameData.rules = new Rules { CityRoadRequiredTech = RoadTech };

		Player player = MakePlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		startTile.overlays.Add(road);

		Tile destination = AddRoadedDestination();

		// The owner lacks the synthetic road technology, so the city tile
		// contributes no road and the destination's terrain cost applies.
		Assert.Equal(1.0,
			TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void ACityTilesRoadParticipatesOnceTheOwnerHasTheSyntheticTechnology() {
		EngineStorage.gameData.rules = new Rules { CityRoadRequiredTech = RoadTech };

		Player player = MakePlayer(RoadTech);
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		startTile.overlays.Add(road);

		Tile destination = AddRoadedDestination();

		Assert.Equal(RoadCost,
			TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void ANullRoadTechnologyDisablesTheGate() {
		// The shipped case: rules+0x1A4 is -1, Leader_has_tech(-1) is true, so
		// the owner's technology is never consulted and the raw overlay bit
		// decides. The player here knows nothing at all, which must not matter.
		EngineStorage.gameData.rules = new Rules { CityRoadRequiredTech = null };

		Player player = MakePlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		startTile.overlays.Add(road);

		Tile destination = AddRoadedDestination();

		Assert.Equal(RoadCost,
			TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	// ------------------------------------------------------------------
	// Founding a city.
	// ------------------------------------------------------------------
	[Fact]
	private void FoundingACitySetsTheRoadFlag() {
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.terrainImprovements.Add(railroad);

		Player player = MakePlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));

		// The road is set even though the owner knows no technology: the
		// original checks no road technology at founding.
		Assert.True(startTile.HasRoad());
		Assert.False(startTile.HasRailroad());
	}

	// ------------------------------------------------------------------
	// Founding a city: the terrain's road bonus gates the road entirely.
	// ------------------------------------------------------------------

	[Fact]
	private void FoundingACityOnATerrainWithNoRoadBonusSetsNoRoad() {
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.terrainImprovements.Add(railroad);

		Player player = MakePlayer();
		InitilizeStartTile(MakeNoRoadBonusTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));

		// Tile_get_road_bonus is tested at 0x4ae656 and a zero value jumps over
		// Set_Tile_Flags at 0x4ae658, so a terrain with no road bonus gets
		// neither bit - not even the road the founding callback otherwise sets
		// without consulting any technology.
		Assert.False(startTile.HasRoad());
		Assert.False(startTile.HasRailroad());
	}

	[Fact]
	private void FoundingACityOnATerrainWithNoRoadBonusSetsNoRailroadEither() {
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.terrainImprovements.Add(railroad);

		// An owner who does know the rules' railroad technology is not enough:
		// the jump at 0x4ae658 skips the whole road/railroad write, so the
		// railroad branch is unreachable on a terrain with no road bonus.
		Player player = MakePlayer(RailTech);
		InitilizeStartTile(MakeNoRoadBonusTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));

		Assert.False(startTile.HasRailroad());
		Assert.False(startTile.HasRoad());
	}

	[Fact]
	private void FoundingACityOnATerrainWithARoadBonusOfTwoSetsTheRoad() {
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.terrainImprovements.Add(railroad);

		// RoadsBonus carries a magnitude - it is the commerce a road adds on the
		// terrain - but the original only tests it against zero, so any non-zero
		// value behaves the same. The shipped Intro2 scenario has a terrain with
		// a road bonus of 2, so the magnitude is reachable data, not a
		// hypothetical.
		Tile tile = new(ID.None("")) {
			baseTerrainType = new() { Key = "plains" },
			overlayTerrainType = new() { Key = "plains", movementCost = 1, roadsBonus = 2 },
		};

		Player player = MakePlayer();
		InitilizeStartTile(tile, new TileLocation(50, 50));
		tile.cityAtTile = new City(tile, player, "City", ID.None(""));

		Assert.True(tile.HasRoad());
		Assert.False(tile.HasRailroad());
	}

	[Fact]
	private void TheRoadBonusIsReadFromTheTerrainTheTileCarriesOnEntry() {
		EngineStorage.gameData.terrainImprovements.Add(road);

		// A forest overlay on tundra, with the road bonus on the forest only.
		// The original's constructor contains no terrain clear at all, so the
		// record it reads at 0x4ae651 is the one the tile carries on entry, not
		// the base terrain the fork's foliage clear swaps in.
		Tile tile = new(ID.None("")) {
			baseTerrainType = new() { Key = "tundra", movementCost = 1, roadsBonus = 0 },
			overlayTerrainType = new() {
				Key = "forest",
				movementCost = 2,
				roadsBonus = 1,
				allowedFoliageAction = TerrainType.Civ3FoliageAction.ClearForest,
			},
		};

		Player player = MakePlayer();
		InitilizeStartTile(tile, new TileLocation(50, 50));
		tile.cityAtTile = new City(tile, player, "City", ID.None(""));

		// The clear did happen ...
		Assert.Equal("tundra", tile.overlayTerrainType.Key);
		// ... and the road is there because the forest's bonus was the one read.
		Assert.True(tile.HasRoad());
	}

	[Fact]
	private void AStepOutOfACityOnATerrainWithNoRoadBonusCostsTheTerrain() {
		EngineStorage.gameData.terrainImprovements.Add(road);

		Player player = MakePlayer();
		InitilizeStartTile(MakeNoRoadBonusTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		Assert.False(startTile.HasRoad());

		Tile destination = AddRoadedDestination();

		// A city tile with no road of its own is not a road end, so the step
		// onto the roaded tile costs the terrain rather than a road step
		// (11_movement.md section 3.2).
		Assert.Equal(1.0,
			TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	[Fact]
	private void AStepOutOfACityOnATerrainWithARoadBonusOntoARoadCostsTheRoad() {
		EngineStorage.gameData.terrainImprovements.Add(road);

		Player player = MakePlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		Assert.True(startTile.HasRoad());

		Tile destination = AddRoadedDestination();

		// The shipped plains' road bonus of 1 is what makes this a road step.
		Assert.Equal(RoadCost,
			TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	// ------------------------------------------------------------------
	// The wheeled-impassability lift is the gated predicate too.
	// ------------------------------------------------------------------

	[Fact]
	private void AWheeledUnitCannotCrossACityTileWhoseOwnerCannotUseItsRoad() {
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.rules = new Rules { CityRoadRequiredTech = RoadTech };

		Player player = MakePlayer();
		InitilizeStartTile(MakeHillTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		Assert.False(startTile.HasRoad());

		// The destination is roaded, so only the source's gate decides: section
		// 4.1 tests the road flag through the live Tile_Check_Roads on both
		// ends, and this end's owner cannot use its road yet.
		Tile mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.overlays.Add(road);

		MapUnit wheeled = MakeWheeledLandUnit();
		wheeled.location = startTile;

		Assert.False(wheeled.CanEnter(mountain));
	}

	[Fact]
	private void AWheeledUnitCanCrossACityTileWhoseOwnerCanUseItsRoad() {
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.rules = new Rules { CityRoadRequiredTech = RoadTech };

		Player player = MakePlayer(RoadTech);
		InitilizeStartTile(MakeHillTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		Assert.True(startTile.HasRoad());

		Tile mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.overlays.Add(road);

		MapUnit wheeled = MakeWheeledLandUnit();
		wheeled.location = startTile;

		Assert.True(wheeled.CanEnter(mountain));
	}

	[Fact]
	private void FoundingACityWithTheRailroadTechnologySetsTheRailroadFlag() {
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.terrainImprovements.Add(railroad);

		Player player = MakePlayer(RailTech);
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));

		Assert.True(startTile.HasRailroad());
	}

	[Fact]
	private void AStepOutOfACityTileOntoOpenTerrainCostsTheTerrain() {
		// The city tile carries a road (founding sets it), but the destination
		// does not, so the road discount does not apply: a road is only as good
		// as its weaker end (11_movement.md section 3.2).
		EngineStorage.gameData.terrainImprovements.Add(road);
		EngineStorage.gameData.terrainImprovements.Add(railroad);

		Player player = MakePlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.cityAtTile = new City(startTile, player, "City", ID.None(""));
		Assert.True(startTile.HasRoad());

		Tile destination = AddNeighborsAndUpdateMap(startTile, MakeHillTile(), TileDirection.NORTH);

		Assert.Equal(2.0,
			TilePath.GetMovementCost(player, startTile, TileDirection.NORTH, destination), Tolerance);
	}

	// ------------------------------------------------------------------
	// The cached trade network.
	// ------------------------------------------------------------------

	[Fact]
	private void LearningACityTileGateTechnologyInvalidatesTheTradeNetwork() {
		// City-tile participation is part of the road network, and the network
		// is cached, so learning a gate technology has to drop the cache.
		EngineStorage.gameData.rules = new Rules { CityRoadRequiredTech = RoadTech };
		Tech tech = new() { id = RoadTech, Name = "Synthetic Roads" };
		EngineStorage.gameData.techs = [tech];

		Player player = MakePlayer();
		TradeNetwork before = EngineStorage.gameData.GetTradeNetwork();

		player.GrantTech(EngineStorage.gameData, tech);

		Assert.Contains(RoadTech, player.knownTechs);
		Assert.NotSame(before, EngineStorage.gameData.GetTradeNetwork());
	}

	[Fact]
	private void LearningAnUnrelatedTechnologyKeepsTheTradeNetwork() {
		EngineStorage.gameData.rules = new Rules { CityRoadRequiredTech = RoadTech };
		Tech other = new() { id = ID.FromString("tech-998"), Name = "Unrelated" };
		EngineStorage.gameData.techs = [other];

		Player player = MakePlayer();
		TradeNetwork before = EngineStorage.gameData.GetTradeNetwork();

		player.GrantTech(EngineStorage.gameData, other);

		Assert.Contains(other.id, player.knownTechs);
		Assert.Same(before, EngineStorage.gameData.GetTradeNetwork());
	}
}

/// <summary>
/// The two city-tile technologies have to exist on BOTH paths: the BIQ import
/// (ImportTerraforms copies them out of the Road and Railroad terrain
/// improvement records) and C7/Lua/civ3/ruleset.json. A feature that reads
/// only one of them is the mistake this project has shipped three times.
/// </summary>
public class CityTileTechGateDataTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGame save;
	private readonly BehaviorEngine behaviors;

	public CityTileTechGateDataTest(SaveGameFixture fixture) {
		save = fixture.saveGame;
		behaviors = fixture.behaviors;
	}

	[Fact]
	public void TheShippedRulesetCarriesTheCityTileTechnologies() {
		// The shipped road field is -1, which the fork stores as an absent
		// technology: Player.HasTech(null) is true, the same answer
		// Leader_has_tech gives for -1, so the shipped road gate is inert.
		Assert.Null(save.Rules.CityRoadRequiredTech);

		// The shipped railroad field is Steam Power (BIQ TFRM[4].Required = 44,
		// ruleset.json "tech-45"), so the shipped railroad gate is live.
		Assert.Equal("tech-45", save.Rules.CityRailroadRequiredTech?.ToString());
		Assert.Equal("Steam Power",
			save.Techs.Single(t => t.id == save.Rules.CityRailroadRequiredTech).Name);
	}

	[Fact]
	public void TheCityTileTechnologiesReachTheRuntimeRules() {
		Rules rules = save.ToGameData(behaviors).rules;

		Assert.Null(rules.CityRoadRequiredTech);
		Assert.Equal("tech-45", rules.CityRailroadRequiredTech?.ToString());
	}

	[SkippableFact]
	public void ImportBiqCarriesTheCityTileTechnologies() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// The base conquests.biq has no Wmap and cannot be imported on its own,
		// so use a shipped scenario that can. Its own TFRM records decide the
		// values: road is -1 and railroad is 30 (Hidden Railroads), which is not
		// the base rules' Steam Power - exactly the case a hard-coded value
		// would get wrong.
		EngineStorage.animationsEnabled = false;
		SaveGame imported = ImportCiv3.ImportBiq(
			ScenarioPath("Conquests", "Intro2 The Three Sisters.biq"),
			PathUtils.defaultBicPath,
			GetPediaIconsPath("Conquests/Conquests"));

		Assert.Null(imported.Rules.CityRoadRequiredTech);
		Assert.Equal("Hidden Railroads",
			imported.Techs.Single(t => t.id == imported.Rules.CityRailroadRequiredTech).Name);
	}

	[SkippableFact]
	public void ImportBiqCarriesTheTerrainRoadBonuses() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// The same scenario the city-tile technologies use. Its own TERR records
		// carry the road bonus, and they are not the base rules': Mountains is 2
		// (the magnitude the founding callback only tests against zero), and its
		// Rain Forest - index 7, the fork's "forest" - is 0.
		EngineStorage.animationsEnabled = false;
		SaveGame imported = ImportCiv3.ImportBiq(
			ScenarioPath("Conquests", "Intro2 The Three Sisters.biq"),
			PathUtils.defaultBicPath,
			GetPediaIconsPath("Conquests/Conquests"));

		Assert.Equal(2, imported.TerrainTypes.Single(t => t.Key == "mountains").roadsBonus);
		Assert.Equal(0, imported.TerrainTypes.Single(t => t.Key == "forest").roadsBonus);
		Assert.Equal(1, imported.TerrainTypes.Single(t => t.Key == "plains").roadsBonus);
	}

	[Fact]
	public void TheShippedRulesetCarriesTheTerrainRoadBonuses() {
		// The shipped conquests.biq's TERR.RoadBonus, the terrain record field at
		// +0x54 that Tile_get_road_bonus reads: 1 on the ten land terrains and 0
		// on volcano and the three water terrains. Founding a city reads this
		// value, so it has to be on the ruleset path as well as the BIQ path.
		foreach (string key in new string[] {
			"desert", "plains", "grassland", "tundra", "flood plain",
			"hills", "mountains", "forest", "jungle", "marsh" }) {
			Assert.Equal(1, save.TerrainTypes.Single(t => t.Key == key).roadsBonus);
		}

		foreach (string key in new string[] { "volcano", "coast", "sea", "ocean" }) {
			Assert.Equal(0, save.TerrainTypes.Single(t => t.Key == key).roadsBonus);
		}
	}

	private static string ScenarioPath(string subfolder, string fileName) {
		return Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", subfolder, fileName);
	}

	private static Func<string, string> GetPediaIconsPath(string subfolder) {
		return (relativeModPath) => {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				relativeModPath = relativeModPath.Replace("\\conquests\\", "/Conquests/");
			}

			return Path.GetFullPath(Path.Combine(
				Civ3Location.GetCiv3Path(), subfolder, relativeModPath, "Text", "PediaIcons.txt"));
		};
	}
}
