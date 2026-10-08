using C7GameData;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

// The flood plain vegetation is chosen by which of a tile's four edges carry a
// river, and the renderer has to ask about an edge rather than read one side's
// bit: a map Civ3 generated records the crossing on both of the tiles sharing
// the edge, while an imported save may record it on only one of them.
public class TileRiverEdgeTest : MapBase {
	// The four edges the flood plain vegetation sheet is indexed by.
	private static readonly TileDirection[] diagonalDirections = {
		TileDirection.NORTHWEST,
		TileDirection.NORTHEAST,
		TileDirection.SOUTHWEST,
		TileDirection.SOUTHEAST,
	};

	[Theory]
	[InlineData(TileDirection.NORTHWEST, TileDirection.SOUTHEAST)]
	[InlineData(TileDirection.NORTHEAST, TileDirection.SOUTHWEST)]
	[InlineData(TileDirection.SOUTHWEST, TileDirection.NORTHEAST)]
	[InlineData(TileDirection.SOUTHEAST, TileDirection.NORTHWEST)]
	public void ACrossingStoredOnThisTileCountsForItsEdge(TileDirection dir, TileDirection opposite) {
		Tile tile = MakeDesertTile();
		SetRiverOnEdge(tile, dir);

		Assert.Equal(opposite, dir.Reversed());
		Assert.True(tile.HasRiverOnEdge(dir));

		// The three edges on the other side of the tile stay clear.
		foreach (TileDirection other in diagonalDirections) {
			if (other != dir) {
				Assert.False(tile.HasRiverOnEdge(other));
			}
		}
	}

	[Theory]
	[InlineData(TileDirection.NORTHWEST, TileDirection.SOUTHEAST)]
	[InlineData(TileDirection.NORTHEAST, TileDirection.SOUTHWEST)]
	[InlineData(TileDirection.SOUTHWEST, TileDirection.NORTHEAST)]
	[InlineData(TileDirection.SOUTHEAST, TileDirection.NORTHWEST)]
	public void ACrossingStoredOnTheNeighbourCountsForTheSharedEdge(TileDirection dir, TileDirection opposite) {
		Tile tile = MakeDesertTile();
		Tile neighbor = AddNeighborsAndUpdateMap(tile, MakeDesertTile(), dir);
		SetRiverOnEdge(neighbor, opposite);

		Assert.True(tile.HasRiverOnEdge(dir));

		// Both tiles report the one crossing, each in its own direction.
		Assert.True(neighbor.HasRiverOnEdge(opposite));
		Assert.False(tile.HasRiverOnEdge(opposite));
	}

	[Fact]
	public void AllFourEdgeCrossingsAreReported() {
		Tile tile = MakeDesertTile();
		foreach (TileDirection dir in diagonalDirections) {
			SetRiverOnEdge(tile, dir);
		}

		foreach (TileDirection dir in diagonalDirections) {
			Assert.True(tile.HasRiverOnEdge(dir));
		}
	}

	[Fact]
	public void ACrossingOnAnotherEdgeIsNotReported() {
		Tile tile = MakeDesertTile();
		tile.riverNortheast = true;

		Assert.True(tile.HasRiverOnEdge(TileDirection.NORTHEAST));
		Assert.False(tile.HasRiverOnEdge(TileDirection.NORTHWEST));
		Assert.False(tile.HasRiverOnEdge(TileDirection.SOUTHWEST));
		Assert.False(tile.HasRiverOnEdge(TileDirection.SOUTHEAST));
	}

	[Fact]
	public void ATileWithoutANeighbourInThatDirectionHasNoRiverOnTheEdge() {
		Tile tile = MakeDesertTile();

		foreach (TileDirection dir in diagonalDirections) {
			Assert.False(tile.HasRiverOnEdge(dir));
		}
	}

	[Fact]
	public void AnUnexploredNeighbourHasNoRiverOnTheEdge() {
		Tile tile = MakeDesertTile();
		tile.neighbors[TileDirection.SOUTHWEST] = Tile.NONE;

		Assert.False(tile.HasRiverOnEdge(TileDirection.SOUTHWEST));
	}

	private static void SetRiverOnEdge(Tile tile, TileDirection dir) {
		switch (dir) {
			case TileDirection.NORTH: tile.riverNorth = true; break;
			case TileDirection.NORTHEAST: tile.riverNortheast = true; break;
			case TileDirection.EAST: tile.riverEast = true; break;
			case TileDirection.SOUTHEAST: tile.riverSoutheast = true; break;
			case TileDirection.SOUTH: tile.riverSouth = true; break;
			case TileDirection.SOUTHWEST: tile.riverSouthwest = true; break;
			case TileDirection.WEST: tile.riverWest = true; break;
			case TileDirection.NORTHWEST: tile.riverNorthwest = true; break;
		}
	}
}
