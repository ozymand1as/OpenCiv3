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

	// The flood plain vegetation sheet holds one 128x64 cell per combination of
	// river edges; Tile.FloodPlainOverlayIndex is the index of the cell, laid
	// out as column index % 4, row index / 4. Cell 1 puts its opaque pixels in
	// the upper left quadrant of the cell and cell 4 in the lower left one, so
	// a river on the north-west edge is index 1, the tile's own upper left
	// corner, and not the vertically mirrored index 4.
	[Theory]
	[InlineData(TileDirection.NORTHWEST, 1)]
	[InlineData(TileDirection.NORTHEAST, 2)]
	[InlineData(TileDirection.SOUTHWEST, 4)]
	[InlineData(TileDirection.SOUTHEAST, 8)]
	public void OneRiverEdgeSelectsItsOwnQuadrantOfTheSheet(TileDirection dir, int expectedIndex) {
		Tile tile = MakeDesertTile();
		SetRiverOnEdge(tile, dir);

		Assert.Equal(expectedIndex, tile.FloodPlainOverlayIndex());
	}

	[Fact]
	public void TwoRiverEdgesSelectTheSumOfTheirQuadrants() {
		Tile tile = MakeDesertTile();
		SetRiverOnEdge(tile, TileDirection.NORTHWEST);
		SetRiverOnEdge(tile, TileDirection.SOUTHWEST);

		Assert.Equal(5, tile.FloodPlainOverlayIndex());
	}

	[Fact]
	public void AllFourRiverEdgesSelectTheLastCellOfTheSheet() {
		Tile tile = MakeDesertTile();
		foreach (TileDirection dir in diagonalDirections) {
			SetRiverOnEdge(tile, dir);
		}

		Assert.Equal(15, tile.FloodPlainOverlayIndex());
	}

	[Fact]
	public void ATileWithoutARiverDrawsNoFloodPlainVegetation() {
		Assert.Equal(0, MakeDesertTile().FloodPlainOverlayIndex());
	}

	[Fact]
	public void ACrossingRecordedOnlyOnTheNeighbourStillSelectsTheQuadrant() {
		Tile tile = MakeDesertTile();
		Tile neighbor = AddNeighborsAndUpdateMap(tile, MakeDesertTile(), TileDirection.SOUTHWEST);
		SetRiverOnEdge(neighbor, TileDirection.NORTHEAST);

		Assert.Equal(4, tile.FloodPlainOverlayIndex());
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
