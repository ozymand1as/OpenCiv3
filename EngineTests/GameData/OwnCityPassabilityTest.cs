using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The passability predicate's early exit (11_movement.md §4.1): if the
/// destination tile is inside a city owned by the mover's own civ, the step is
/// allowed whatever the terrain says. It comes before the terrain test, so a
/// wheeled unit may enter its own mountain city - while a foreign mountain city
/// still refuses it, and the same mountain without a city still refuses it.
/// </summary>
public sealed class OwnCityPassabilityTest : MapBase {
	public OwnCityPassabilityTest() {
		// Building a city touches the tile's improvements and the trade network.
		InitializeTestGameData();
	}

	private Player MakeCivPlayer() {
		Player player = MakePlayer();
		player.civilization = new Civilization();
		return player;
	}

	[Fact]
	public void AWheeledUnitMayEnterItsOwnMountainCity() {
		Player owner = MakeCivPlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.cityAtTile = new City(mountain, owner, "Capital", ID.None("city"));

		MapUnit wheeled = MakeWheeledLandUnit();
		wheeled.owner = owner;
		wheeled.location = startTile;

		// Mountains are impassable to wheeled units, but the destination is the
		// unit's own city, so the early exit allows the step before the terrain
		// test runs.
		Assert.True(wheeled.CanEnter(mountain));
		Assert.True(wheeled.CanEnterForcefully(mountain));
	}

	[Fact]
	public void AWheeledUnitMayEnterItsOwnFullyImpassableCity() {
		Player owner = MakeCivPlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var desert = AddNeighborsAndUpdateMap(startTile, MakeImpassableDesertTile(), TileDirection.NORTH);
		desert.cityAtTile = new City(desert, owner, "Capital", ID.None("city"));

		MapUnit wheeled = MakeWheeledLandUnit();
		wheeled.owner = owner;
		wheeled.location = startTile;

		// "Regardless of terrain" covers the absolute Impassable flag too: a
		// mod that makes a terrain impassable still lets a civ reach its own
		// city on it.
		Assert.True(wheeled.CanEnter(desert));
	}

	[Fact]
	public void AWheeledUnitStillCannotEnterAForeignMountainCity() {
		Player mover = MakeCivPlayer();
		Player other = MakeCivPlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.cityAtTile = new City(mountain, other, "Foreign Capital", ID.None("city"));

		MapUnit wheeled = MakeWheeledLandUnit();
		wheeled.owner = mover;
		wheeled.location = startTile;

		// The early exit is for the mover's own city only.
		Assert.False(wheeled.CanEnter(mountain));
		Assert.False(wheeled.CanEnterForcefully(mountain));
	}

	[Fact]
	public void AWheeledUnitStillCannotEnterAnUncityedMountain() {
		Player owner = MakeCivPlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);

		MapUnit wheeled = MakeWheeledLandUnit();
		wheeled.owner = owner;
		wheeled.location = startTile;

		Assert.False(wheeled.CanEnter(mountain));
	}

	[Fact]
	public void AnOwnCityOnAnUnroadedMountainIsEnterableByEveryUnit() {
		Player owner = MakeCivPlayer();
		InitilizeStartTile(MakePlainsTile(), new TileLocation(50, 50));
		var mountain = AddNeighborsAndUpdateMap(startTile, MakeMountainTile(), TileDirection.NORTH);
		mountain.cityAtTile = new City(mountain, owner, "Capital", ID.None("city"));

		MapUnit foot = MakeLandUnit();
		foot.owner = owner;
		foot.location = startTile;

		Assert.True(foot.CanEnter(mountain));
	}
}
