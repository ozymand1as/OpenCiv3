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
		Assert.Equal(4, tile.RiverOverlayIndex());
	}

	// The river sheet holds one 128x64 cell per combination of the tile's four
	// river edges, laid out as column index % 4, row index / 4, and the art says
	// which combination each cell is: cell 1's opaque pixels hug the upper-left
	// edge of its cell, cell 2 the upper-right, cell 4 the lower-left and cell 8
	// the lower-right, while cell 6's river runs from the lower-left edge
	// midpoint through the cell centre to the upper-right one. A river crossing
	// a tile's south-west edge and leaving by its north-east edge is therefore
	// cell 6 - the tile's own picture of the river that crosses it - and any
	// other cell for that tile, or that cell drawn half a tile away, is the
	// perpendicular graphics the bug report describes.
	[Theory]
	[InlineData(TileDirection.NORTHWEST, 1)]
	[InlineData(TileDirection.NORTHEAST, 2)]
	[InlineData(TileDirection.SOUTHWEST, 4)]
	[InlineData(TileDirection.SOUTHEAST, 8)]
	public void OneRiverEdgeSelectsTheCellItsArtworkHugs(TileDirection dir, int expectedIndex) {
		Tile tile = MakeDesertTile();
		SetRiverOnEdge(tile, dir);

		Assert.Equal(expectedIndex, tile.RiverOverlayIndex());
	}

	// Rule 2.9.19 writes the pair of bits of its diagonal on both of the tiles a
	// step passes, so a tile a river only reaches still carries the bit of the
	// edge it does not cross. The neighbour across an edge records that edge when
	// the river really is there, and that mark is what the frame is read from.
	[Theory]
	[InlineData(TileDirection.NORTHWEST, 1)]
	[InlineData(TileDirection.NORTHEAST, 2)]
	[InlineData(TileDirection.SOUTHWEST, 4)]
	[InlineData(TileDirection.SOUTHEAST, 8)]
	public void AnEdgeBothTilesRecordSelectsThatEdge(TileDirection dir, int expectedIndex) {
		Tile tile = MakeDesertTile();
		SetRiverEdgeBothSides(tile, dir);

		Assert.Equal(expectedIndex, tile.RiverOverlayIndex());
	}

	// A run of river crosses two opposite edges, which is the shape the
	// generator writes for every tile a straight run passes: one pair of bits on
	// each of the tiles of the step. The two runs select the two through-cells
	// (6 for the south-west to north-east run, 9 for the north-west to
	// south-east one).
	[Theory]
	[InlineData(TileDirection.SOUTHWEST, TileDirection.NORTHEAST, 6)]
	[InlineData(TileDirection.NORTHWEST, TileDirection.SOUTHEAST, 9)]
	public void AStraightRunSelectsTheCellTheRiverRunsThrough(TileDirection first, TileDirection second, int expectedIndex) {
		Tile tile = MakeDesertTile();
		SetRiverEdgeBothSides(tile, first);
		SetRiverEdgeBothSides(tile, second);

		Assert.Equal(expectedIndex, tile.RiverOverlayIndex());
	}

	// The four corners: a river that crosses two adjacent edges of the tile turns
	// inside it, and the sheet has one cell per corner that bends that way.
	[Theory]
	[InlineData(TileDirection.NORTHWEST, TileDirection.NORTHEAST, 3)]
	[InlineData(TileDirection.NORTHEAST, TileDirection.SOUTHEAST, 10)]
	[InlineData(TileDirection.SOUTHWEST, TileDirection.SOUTHEAST, 12)]
	[InlineData(TileDirection.NORTHWEST, TileDirection.SOUTHWEST, 5)]
	public void ATurnSelectsTheCellForThatCorner(TileDirection first, TileDirection second, int expectedIndex) {
		Tile tile = MakeDesertTile();
		SetRiverEdgeBothSides(tile, first);
		SetRiverEdgeBothSides(tile, second);

		Assert.Equal(expectedIndex, tile.RiverOverlayIndex());
	}

	// A tile the generator laid a bend through carries all four bits: the step
	// into it wrote one pair and the step out of it the other, and only the two
	// edges it really crossed have a mark on the tile across them. The frame is
	// the two real edges (here the south corner, cell 12) and not all four edges
	// (cell 15), which would draw a four-way crossing where the river bends.
	[Fact]
	public void ABendIsTheTwoEdgesTheRiverCrossesAndNotTheBitsThePairsCarry() {
		Tile tile = MakeDesertTile();
		foreach (TileDirection dir in diagonalDirections) {
			SetRiverOnEdge(tile, dir);
		}

		// The bend runs from the south-west edge round the south corner to the
		// south-east edge, so those are the two the neighbouring tiles record.
		RecordAcrossTheEdge(tile, TileDirection.SOUTHWEST);
		RecordAcrossTheEdge(tile, TileDirection.SOUTHEAST);

		Assert.Equal(12, tile.RiverOverlayIndex());
	}

	// A river that ends in a tile writes its last pair there, so the tile carries
	// the bit of an edge no river crosses and only the edge back along the river
	// has a mark on the other side. The frame shows the lone edge the river does
	// reach the tile by, not a river drawn straight through it.
	[Fact]
	public void ARiverEndSelectsTheOneEdgeTheRiverReachesTheTileBy() {
		Tile tile = MakeDesertTile();
		SetRiverOnEdge(tile, TileDirection.NORTHEAST);
		SetRiverOnEdge(tile, TileDirection.SOUTHWEST);
		RecordAcrossTheEdge(tile, TileDirection.SOUTHWEST);

		Assert.Equal(4, tile.RiverOverlayIndex());
	}

	[Fact]
	public void ThreeRiverEdgesSelectOneJunctionCell() {
		Tile tile = MakeDesertTile();
		SetRiverEdgeBothSides(tile, TileDirection.NORTHWEST);
		SetRiverEdgeBothSides(tile, TileDirection.NORTHEAST);
		SetRiverEdgeBothSides(tile, TileDirection.SOUTHWEST);

		Assert.Equal(7, tile.RiverOverlayIndex());
	}

	[Fact]
	public void AllFourRiverEdgesSelectTheLastCellOfTheRiverSheet() {
		Tile tile = MakeDesertTile();
		foreach (TileDirection dir in diagonalDirections) {
			SetRiverEdgeBothSides(tile, dir);
		}

		Assert.Equal(15, tile.RiverOverlayIndex());
	}

	[Fact]
	public void ATileWithoutARiverDrawsNoRiverCell() {
		Assert.Equal(0, MakeDesertTile().RiverOverlayIndex());
	}

	// Civ3 draws the river and the flood plain vegetation from one call, on the
	// same cell, so the two layers have to select the same cell for every
	// combination of edges. One mapping for both is the point of the two methods
	// sharing a body, and this is the check that they cannot drift apart.
	[Fact]
	public void TheRiverAndTheFloodPlainVegetationSelectTheSameCell() {
		for (int mask = 0; mask < 16; ++mask) {
			Tile tile = MakeDesertTile();
			for (int i = 0; i < diagonalDirections.Length; ++i) {
				if ((mask & (1 << i)) != 0) {
					SetRiverEdgeBothSides(tile, diagonalDirections[i]);
				}
			}

			Assert.Equal(mask, tile.RiverOverlayIndex());
			Assert.Equal(tile.RiverOverlayIndex(), tile.FloodPlainOverlayIndex());
		}
	}

	// Records the crossing of `dir` on the tile across that edge, which is where
	// a river that crosses the edge records it.
	private void RecordAcrossTheEdge(Tile tile, TileDirection dir) {
		Tile neighbor = AddNeighborsAndUpdateMap(tile, MakeDesertTile(), dir);
		SetRiverOnEdge(neighbor, dir.Reversed());
	}

	private void SetRiverEdgeBothSides(Tile tile, TileDirection dir) {
		SetRiverOnEdge(tile, dir);
		RecordAcrossTheEdge(tile, dir);
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
