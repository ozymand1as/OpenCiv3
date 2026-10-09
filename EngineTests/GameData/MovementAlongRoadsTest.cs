using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards the movement scale, Civ3's RULE.MovementAlongRoads
/// (11_movement.md §2.1).
///
/// Movement points are internal units: one whole point is MovementAlongRoads
/// of them, so a road step costs 1 / the scale and a unit's maximum movement is
/// its rate times the scale. OpenCiv3 used to hard-code the fraction
/// 0.33333334 in ruleset.json and never read the rule, so a scenario with a
/// different scale moved at the wrong rate. The shipped data is not uniform:
/// probed from the install, conquests.biq and most scenarios use 3, Intro2 The
/// Three Sisters uses 2 and Scenarios/2 MP Rise of Rome uses 4.
/// </summary>
public class MovementAlongRoadsTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly SaveGame save;

	public MovementAlongRoadsTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		save = fixture.saveGame;
	}

	[Fact]
	public void TheShippedRulesetCarriesTheScale() {
		Assert.Equal(3, save.Rules.MovementAlongRoads);
		Assert.Equal(3, fixture.standaloneSaveGame.Rules.MovementAlongRoads);
	}

	[Fact]
	public void TheScaleReachesTheRuntimeRules() {
		// The movement code reads the runtime rules object, so the value has to
		// make it across from the save data.
		Assert.Equal(3, save.ToGameData(fixture.behaviors).rules.MovementAlongRoads);
	}

	[Fact]
	public void TheImprovementCostsAreInInternalMovementUnits() {
		// One movement point is three internal units, the road is one of them,
		// and a railroad is free. The old fraction 0.33333334 is gone: the scale
		// is what turns the internal cost into a movement cost.
		Assert.Equal(1, save.TerrainImprovements.Single(i => i.key == Tile.TileOverlays.ROAD).movementCost);
		Assert.Equal(0, save.TerrainImprovements.Single(i => i.key == Tile.TileOverlays.RAILROAD).movementCost);

		// The BIQ-independent improvement list agrees, so a game with no BIQ
		// gets the same road.
		Assert.Equal(1, SaveTerrainImprovement.Civ3Improvements().Single(i => i.key == Tile.TileOverlays.ROAD).movementCost);
		Assert.Equal(0, SaveTerrainImprovement.Civ3Improvements().Single(i => i.key == Tile.TileOverlays.RAILROAD).movementCost);
	}

	[SkippableFact]
	public void TheShippedBiqsCarryTheScale() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// The scale is not uniform across the shipped files, which is exactly
		// why it cannot be hard-coded.
		Assert.Equal(3, BiqData.LoadFile(PathUtils.defaultBicPath).Rule[0].MovementAlongRoads);
		Assert.Equal(2, BiqData.LoadFile(ScenarioPath("Conquests", "Intro2 The Three Sisters.biq")).Rule[0].MovementAlongRoads);
		Assert.Equal(4, BiqData.LoadFile(ScenarioPath("Scenarios", "2 MP Rise of Rome.biq")).Rule[0].MovementAlongRoads);
	}

	[SkippableFact]
	public void ImportBiqCarriesTheScale() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		// A shipped scenario whose own RULE record says 2, through the whole
		// import path: a BIQ game must not play at the base game's 3.
		EngineStorage.animationsEnabled = false;
		SaveGame imported = ImportCiv3.ImportBiq(
			ScenarioPath("Conquests", "Intro2 The Three Sisters.biq"),
			PathUtils.defaultBicPath,
			GetPediaIconsPath("Conquests/Conquests"));

		Assert.Equal(2, imported.Rules.MovementAlongRoads);
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

/// <summary>
/// The scale's effect on the road and railroad step costs. The terrain costs
/// are already in movement points and do not move with the scale; only the
/// improvements' internal costs do.
/// </summary>
public sealed class MovementScaleMovementTest : MapBase {
	private const double Tolerance = 0.0001;

	public MovementScaleMovementTest() {
		InitializeTestGameData();
	}

	private static void SetScale(int scale) {
		EngineStorage.gameData.rules = new Rules { MovementAlongRoads = scale };
	}

	[Fact]
	public void TheShippedScaleGivesAThirdOfAMovementPointPerRoadStep() {
		SetScale(Rules.DefaultMovementAlongRoads);
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.overlays.Add(road);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;

		Assert.Equal(1.0 / 3.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit), Tolerance);
	}

	[Fact]
	public void ChangingTheScaleChangesTheRoadStep() {
		SetScale(6);
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.overlays.Add(road);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;

		// One internal unit on a scale of six is 1/6 of a movement point.
		Assert.Equal(1.0 / 6.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit), Tolerance);
	}

	[Fact]
	public void ChangingTheScaleChangesARailroadToRoadStep() {
		SetScale(4);
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(railroad);
		var plains = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		plains.overlays.Add(road);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;

		// The slower end wins, and its internal cost is scaled too.
		Assert.Equal(1.0 / 4.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, plains, unit), Tolerance);
	}

	[Fact]
	public void RailroadsStayFreeWhateverTheScale() {
		SetScale(4);
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(railroad);
		var plains = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		plains.overlays.Add(railroad);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;

		Assert.Equal(0.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, plains, unit), Tolerance);
	}

	[Fact]
	public void TheScaleDoesNotChangeTheTerrainCost() {
		SetScale(6);
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;

		// Terrain costs are already in movement points; only the improvements'
		// internal costs are scaled.
		Assert.Equal(3.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, mountain, unit), Tolerance);
	}

	[Fact]
	public void WithNoRulesLoadedTheShippedScaleApplies() {
		// A test that never sets up rules must not divide by zero, and must not
		// silently move at a different rate either.
		EngineStorage.gameData.rules = null;
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		startTile.overlays.Add(road);
		var plains = AddNeighborsAndUpdateMap(startTile, MakePlainsTile(), TileDirection.NORTH);
		plains.overlays.Add(road);

		MapUnit unit = MakeLandUnit(movementPoints: 2);
		unit.location = startTile;

		Assert.Equal(1.0 / 3.0, TilePath.GetMovementCost(unit.owner, startTile, TileDirection.NORTH, plains, unit), Tolerance);
	}
}
