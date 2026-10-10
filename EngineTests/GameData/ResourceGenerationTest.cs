using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using Xunit;

namespace EngineTests.GameData;

// The resource pass at `0x5f22a0` and the candidacy predicate it asks through
// the world vtable slot at `+0x44` (`Map_can_spawn_resource_at` @ `0x5f3320`).
// The rules, the addresses they were read from and the fork's old divergences
// are written up in re/notes/civ3_map_generator_spec.md section 2.11.
//
// The rules these tests pin down, and what the fork used to do instead:
//
//   * the unset appearance ratio is 50 plus two draws over 0..25, a triangular
//     50..100; the fork added five draws over 0..10, the same range with a
//     higher-biased shape;
//   * a candidate's spacing obeys its class: among the eight neighbours any
//     other resource blocks, and holding this very resource blocks only a
//     strategic one; further out a strategic resource minds only its own kind
//     and a luxury only a different luxury. The fork refused a candidate when
//     any neighbour held any resource, for every class;
//   * luxuries additionally refuse a clump of three or more of the eight
//     neighbours already holding the same resource (two are fine);
//   * terrains past index ten count five times when the eligible terrain weight
//     is taken, and that weight scales the target (halved, or three quarters)
//     and drives the bonus pass' die;
//   * the predicate refuses a tile that carries the potential shield bonus -
//     one bit of the tile's flag word, not the other flags that share it -
//     and minds no other tile flag;
//   * the resource records are walked in record order - only the tile order is
//     shuffled.
public class ResourceGenerationTest {
	// ------------------------------------------------------------------ setup

	// Twelve terrains, so that there is an index past ten, and so that a
	// resource can be made rare or common by which of them it may stand on.
	private static List<TerrainType> TerrainTypes(int exoticIndex) {
		List<TerrainType> result = new();
		for (int i = 0; i < 12; ++i) {
			result.Add(new TerrainType() { Key = (i == exoticIndex) ? "grassland" : $"filler{i}" });
		}
		return result;
	}

	private static void Allow(List<TerrainType> terrainTypes, Resource r) {
		terrainTypes[11].allowedResources.Add(r.Key);
	}

	private static Resource MakeResource(string key, ResourceCategory category, int appearanceRatio) {
		return new Resource() { Key = key, Name = key, Category = category, AppearanceRatio = appearanceRatio };
	}

	// Builds a map with the tile ordering the generator uses, every tile on the
	// filler terrain. Tile coordinates are the same doubled grid a generated
	// map uses, so `Civ3SpiralOffset` and `RankDistanceTo` mean here what they
	// mean there.
	private static GameMap MakeMap(int width, int height, TerrainType fill) {
		GameMap m = new() {
			tiles = new List<Tile>(),
			numTilesWide = width,
			numTilesTall = height,
		};

		ID.Factory factory = new();

		for (int y = 0; y < height; ++y) {
			for (int x = y % 2; x < width; x += 2) {
				Tile t = new(factory.CreateID("tile"));
				t.XCoordinate = x;
				t.YCoordinate = y;
				t.baseTerrainType = fill;
				t.overlayTerrainType = fill;
				m.tiles.Add(t);
			}
		}

		m.computeNeighbors();
		m.recomputeContinents();
		return m;
	}

	private static WorldCharacteristics MakeWc(GameMap m, List<TerrainType> terrainTypes, List<Resource> resources, int seed) {
		return new WorldCharacteristics() {
			mapSeed = seed,
			worldSize = new WorldSize() {
				width = m.numTilesWide,
				height = m.numTilesTall,
				numberOfCivs = 8,
				distanceBetweenCivs = 12,
			},
			terrainTypes = terrainTypes,
			resources = resources,
			maxRankOfWorkableTiles = 2,
			maxRankOfBarbarianCampTiles = 2,
		};
	}

	private static Tile At(GameMap m, int x, int y) {
		Tile t = m.tileAt(x, y);
		Assert.True(t != Tile.NONE, $"no tile at ({x},{y})");
		return t;
	}

	// A tile inside the map, far enough from every edge that all of its first
	// two rings exist. Even rows hold the even x coordinates, so the centre is
	// an even x on an even row.
	private static Tile Centre(GameMap m) {
		return At(m, 10, 8);
	}

	private static void MakeGrassland(List<TerrainType> terrainTypes, Tile t) {
		t.overlayTerrainType = terrainTypes[11];
		t.baseTerrainType = terrainTypes[11];
	}

	private static void PutResource(Tile t, Resource r) {
		t.Resource = r;
		t.ResourceKey = r.Key;
	}

	private static string ResourceSignature(GameMap m) {
		return string.Join(";", m.tiles.Select(t => $"{t.XCoordinate},{t.YCoordinate}={t.Resource?.Key ?? "-"}"));
	}

	// -------------------------------------------------- the clump and spacing

	// The fork refused a candidate when any of its eight neighbours held any
	// resource. Civ3 only minds a different resource among the eight, so the
	// same resource twice is fine - it is three that make a clump.
	[Fact]
	public void TwoOfEightSameResourceNeighboursAreAccepted() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource luxury = MakeResource("Gems", ResourceCategory.LUXURY, 160);
		Allow(terrainTypes, luxury);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		Tile centre = Centre(m);
		MakeGrassland(terrainTypes, centre);

		List<Tile> neighbours = centre.neighbors.Values.Where(x => x != null && x != Tile.NONE).ToList();
		Assert.Equal(8, neighbours.Count);
		PutResource(neighbours[0], luxury);
		PutResource(neighbours[4], luxury);

		WorldCharacteristics wc = MakeWc(m, terrainTypes, [luxury], seed: 20260214);

		Assert.False(MapGenerator.ClumpGuardRefuses(centre, luxury));
		Assert.True(MapGenerator.CanPlaceResource(wc, m, luxury, centre, false),
			"two of the eight neighbours hold this luxury; the class rule allows it");
	}

	// Three of the eight neighbours holding the same resource is the clump the
	// luxury pass' spreading step refuses.
	[Fact]
	public void ThreeOfEightSameResourceNeighboursAreRefused() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource luxury = MakeResource("Gems", ResourceCategory.LUXURY, 160);
		Allow(terrainTypes, luxury);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		Tile centre = Centre(m);
		MakeGrassland(terrainTypes, centre);

		List<Tile> neighbours = centre.neighbors.Values.Where(x => x != null && x != Tile.NONE).ToList();
		PutResource(neighbours[0], luxury);
		PutResource(neighbours[3], luxury);
		PutResource(neighbours[6], luxury);

		Assert.True(MapGenerator.ClumpGuardRefuses(centre, luxury),
			"three of the eight neighbours hold this luxury, which is the clump");
	}

	// A different resource among the eight blocks every class. This is the
	// measured rule; it is NOT "any neighbour is fine as long as it holds
	// something else".
	[Fact]
	public void DifferentResourcesAmongTheEightNeighboursAreRefused() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource luxury = MakeResource("Gems", ResourceCategory.LUXURY, 160);
		Resource bonus = MakeResource("Wheat", ResourceCategory.BONUS, 160);
		Allow(terrainTypes, luxury);
		Allow(terrainTypes, bonus);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		Tile centre = Centre(m);
		MakeGrassland(terrainTypes, centre);

		List<Tile> neighbours = centre.neighbors.Values.Where(x => x != null && x != Tile.NONE).ToList();
		PutResource(neighbours[0], luxury);
		PutResource(neighbours[2], bonus);

		WorldCharacteristics wc = MakeWc(m, terrainTypes, [luxury, bonus], seed: 20260214);

		Assert.False(MapGenerator.ClumpGuardRefuses(centre, luxury));
		Assert.False(MapGenerator.CanPlaceResource(wc, m, luxury, centre, false),
			"a different resource among the eight neighbours blocks a luxury");
	}

	// Further out than the eight neighbours the class rules differ: a strategic
	// resource minds only its own kind, so a different resource is allowed. The
	// fork's "no resource within the spacing radius" rule refused both.
	[Fact]
	public void AStrategicResourceAcceptsADifferentResourceTwoRanksOut() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource horses = MakeResource("Horses", ResourceCategory.STRATEGIC, 160);
		Resource wheat = MakeResource("Wheat", ResourceCategory.BONUS, 160);
		Allow(terrainTypes, horses);
		Allow(terrainTypes, wheat);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		Tile centre = Centre(m);
		MakeGrassland(terrainTypes, centre);

		// The first tile of the second ring of the original's spiral.
		(int dx, int dy) = MapGenerator.Civ3SpiralOffset(9);
		Tile twoRanksOut = At(m, centre.XCoordinate + dx, centre.YCoordinate + dy);
		MakeGrassland(terrainTypes, twoRanksOut);
		Assert.Equal(2, centre.RankDistanceTo(twoRanksOut));

		WorldCharacteristics wc = MakeWc(m, terrainTypes, [horses, wheat], seed: 20260214);

		PutResource(twoRanksOut, wheat);
		Assert.True(MapGenerator.CanPlaceResource(wc, m, horses, centre, true),
			"a bonus resource two ranks out does not block a strategic one");

		PutResource(twoRanksOut, horses);
		Assert.False(MapGenerator.CanPlaceResource(wc, m, horses, centre, true),
			"the same strategic resource two ranks out does block");
	}

	// Civ3 shuffles the tile order once and walks the resource records in
	// record order. The fork shuffled the resources too, so which of two
	// competitors won a contested tile was a coin toss.
	[Fact]
	public void ResourcesAreUsedInRecordOrderAndNotShuffled() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource first = MakeResource("Gems", ResourceCategory.LUXURY, 160);
		Resource second = MakeResource("Wines", ResourceCategory.LUXURY, 160);
		Allow(terrainTypes, first);
		Allow(terrainTypes, second);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);

		// Exactly one tile on the whole map can carry either luxury, and it is
		// on a body big enough for the luxury body-size gate.
		Tile onlySpot = Centre(m);
		MakeGrassland(terrainTypes, onlySpot);

		WorldCharacteristics wc = MakeWc(m, terrainTypes, [first, second], seed: 20260214);
		MapGenerator.AddResources(wc, m);

		Assert.Same(first, onlySpot.Resource);
		Assert.Equal(0, m.tiles.Count(t => t.Resource == second));
	}

	// Two adjacent tiles holding the same bonus resource: the fork's
	// "no adjacent resource of any kind" rule made this impossible, while the
	// class rule lets the same resource sit next to itself.
	[Fact]
	public void AdjacentSameBonusResourcesAreAllowed() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource wheat = MakeResource("Wheat", ResourceCategory.BONUS, 100);
		Allow(terrainTypes, wheat);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);

		Tile a = Centre(m);
		Tile b = At(m, a.XCoordinate + 1, a.YCoordinate + 1);
		MakeGrassland(terrainTypes, a);
		MakeGrassland(terrainTypes, b);

		WorldCharacteristics wc = MakeWc(m, terrainTypes, [wheat], seed: 20260214);
		MapGenerator.AddResources(wc, m);

		// The eligible terrain is past index ten, so the weight is five and the
		// bonus pass' die is two-sided: every round places, so both tiles fill.
		Assert.Equal(2, m.tiles.Count(t => t.Resource == wheat));
		Assert.Same(wheat, a.Resource);
		Assert.Same(wheat, b.Resource);
	}

	// ------------------------------------------------------------ appearance

	// The unset ratio is 50 plus two draws over 0..25, so the top of its range
	// is 100 and values near the top are common. The old five-draws-over-0..10
	// version had the same range but could not reach 95 with any realistic
	// frequency: 21 of the 676 outcomes of the new shape are 95 or more, and
	// about 1.3 in 100000 of the old one are.
	[Fact]
	public void UnsetAppearanceRatioIsTwoDrawsOverTwentySix() {
		Random rand = new(4242);
		List<int> draws = new();
		for (int i = 0; i < 500; ++i) {
			draws.Add(MapGenerator.DrawDefaultAppearanceRatio(rand));
		}

		Assert.All(draws, d => Assert.InRange(d, 50, 100));
		int high = draws.Count(d => d >= 95);
		Assert.True(high >= 5, $"only {high} of 500 draws reached 95; two draws over 0..25 expect about 15");
	}

	// A stated ratio is used as it stands.
	[Fact]
	public void AStatedAppearanceRatioIsUsedAsItStands() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource horses = MakeResource("Horses", ResourceCategory.STRATEGIC, 150);
		Allow(terrainTypes, horses);

		// One eligible terrain, at index 11, so a weight of five.
		terrainTypes[11].allowedResources.Remove(horses.Key);
		terrainTypes[3].allowedResources.Add(horses.Key);

		GameMap m = MakeMap(20, 16, terrainTypes[3]);
		WorldCharacteristics wc = MakeWc(m, terrainTypes, [horses], seed: 20260214);

		// Eight civs and a ratio of 150 give 12; one eligible terrain before
		// index ten halves that to 6, floored at one.
		Assert.Equal(1, MapGenerator.TerrainWeight(wc, horses));
		Assert.Equal(6, MapGenerator.GetAppearance(wc, new Random(1), horses, MapGenerator.TerrainWeight(wc, horses)));

		// The same, with the terrain moved past index ten: five times the
		// weight, no scaling, and a floor of two.
		terrainTypes[3].allowedResources.Remove(horses.Key);
		terrainTypes[11].allowedResources.Add(horses.Key);
		Assert.Equal(5, MapGenerator.TerrainWeight(wc, horses));
		Assert.Equal(12, MapGenerator.GetAppearance(wc, new Random(1), horses, 5));
	}

	// -------------------------------------------------------- terrain weights

	// Terrains past index ten count five times, which is what makes water
	// resources easy to place and rare land resources hard.
	[Fact]
	public void TerrainsPastIndexTenCountFiveTimes() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource fish = MakeResource("Fish", ResourceCategory.BONUS, 100);
		Allow(terrainTypes, fish);

		Resource wheat = MakeResource("Wheat", ResourceCategory.BONUS, 100);
		terrainTypes[4].allowedResources.Add(wheat.Key);
		terrainTypes[11].allowedResources.Add(wheat.Key);

		Resource oasis = MakeResource("Oasis", ResourceCategory.BONUS, 10);
		terrainTypes[0].allowedResources.Add(oasis.Key);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		WorldCharacteristics wc = MakeWc(m, terrainTypes, [fish, wheat, oasis], seed: 20260214);

		Assert.Equal(5, MapGenerator.TerrainWeight(wc, fish));
		Assert.Equal(6, MapGenerator.TerrainWeight(wc, wheat));
		Assert.Equal(1, MapGenerator.TerrainWeight(wc, oasis));

		// A weight of four or more does not scale the target and floors it at
		// two; a weight of one halves it and floors it at one.
		Assert.Equal(1, MapGenerator.GetAppearance(wc, new Random(1), oasis, 1));
		Assert.Equal(2, MapGenerator.GetAppearance(wc, new Random(1), oasis, 5));
	}

	// The bonus pass' die follows the same weight: six sides (a one-in-three
	// chance) for a weight of one, four sides for two or three, and two sides
	// for four or more, with a placement attempted on a roll of zero or one.
	[Theory]
	[InlineData(1, 6)]
	[InlineData(2, 4)]
	[InlineData(3, 4)]
	[InlineData(4, 2)]
	[InlineData(6, 2)]
	[InlineData(20, 2)]
	public void TheBonusDieFollowsTheTerrainWeight(int weight, int dieSides) {
		Assert.Equal(dieSides, MapGenerator.BonusDieSides(weight));
	}

	// -------------------------------------------------- the class order

	// The class order is part of the rule: luxuries are placed first, then the
	// strategic resources, then everything that is neither. One tile on the map
	// can carry either resource, and the strategic record is deliberately listed
	// first, so a pass that walked the records without the class split would
	// place the strategic one and fail here.
	[Fact]
	public void LuxuriesArePlacedBeforeStrategicResources() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource gems = MakeResource("Gems", ResourceCategory.LUXURY, 160);
		Resource horses = MakeResource("Horses", ResourceCategory.STRATEGIC, 160);
		Allow(terrainTypes, gems);
		Allow(terrainTypes, horses);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		Tile onlySpot = Centre(m);
		MakeGrassland(terrainTypes, onlySpot);

		WorldCharacteristics wc = MakeWc(m, terrainTypes, [horses, gems], seed: 20260214);
		MapGenerator.AddResources(wc, m);

		Assert.Same(gems, onlySpot.Resource);
		Assert.Equal(0, m.tiles.Count(t => t.Resource == horses));
	}

	// And the strategic pass runs before the bonus one.
	[Fact]
	public void StrategicResourcesArePlacedBeforeBonusResources() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource wheat = MakeResource("Wheat", ResourceCategory.BONUS, 160);
		Resource horses = MakeResource("Horses", ResourceCategory.STRATEGIC, 160);
		Allow(terrainTypes, wheat);
		Allow(terrainTypes, horses);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		Tile onlySpot = Centre(m);
		MakeGrassland(terrainTypes, onlySpot);

		WorldCharacteristics wc = MakeWc(m, terrainTypes, [wheat, horses], seed: 20260214);
		MapGenerator.AddResources(wc, m);

		Assert.Same(horses, onlySpot.Resource);
		Assert.Equal(0, m.tiles.Count(t => t.Resource == wheat));
	}

	// -------------------------------------------------- the walk order

	// The tile order is one seeded shuffle of the whole tile list, made before
	// the first pass and reused by all three. With exactly one placement
	// possible the resource lands on the first tile of that order - not on tile
	// zero, which is what walking the list in index order would give, and not on
	// the first tile of a second shuffle.
	[Fact]
	public void TheTileWalkIsOneSeededShuffleOfTheTileList() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource horses = MakeResource("Horses", ResourceCategory.STRATEGIC, 25);
		// One eligible terrain before index ten, so the weight is one and the
		// target is (8 * 25 / 100) / 2 = 1.
		terrainTypes[3].allowedResources.Add(horses.Key);

		GameMap m = MakeMap(20, 16, terrainTypes[3]);
		WorldCharacteristics wc = MakeWc(m, terrainTypes, [horses], seed: 20260214);
		Assert.Equal(1, MapGenerator.GetAppearance(wc, new Random(1), horses, MapGenerator.TerrainWeight(wc, horses)));

		int[] expectedOrder = Enumerable.Range(0, m.tiles.Count).ToArray();
		new Random(20260214 + MapGenerator.RESOURCE_SEED_OFFSET).Shuffle<int>(expectedOrder.AsSpan());
		Assert.NotEqual(0, expectedOrder[0]);

		MapGenerator.AddResources(wc, m);

		Assert.Same(horses, m.tiles[expectedOrder[0]].Resource);
		Assert.Equal(1, m.tiles.Count(t => t.Resource == horses));
	}

	// ------------------------------------------------------ the clump step

	// The clump step asks for the first of the origin's eight neighbours, in the
	// original's spiral order, that the guard and the predicate both accept. The
	// neighbour at the first spiral position is made ineligible here, so a helper
	// that took the neighbours in any other order - or that ignored the
	// predicate - returns the wrong tile.
	[Fact]
	public void TheClumpStepTakesTheFirstAcceptedNeighbourInSpiralOrder() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource gems = MakeResource("Gems", ResourceCategory.LUXURY, 160);
		Allow(terrainTypes, gems);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		Tile origin = Centre(m);
		MakeGrassland(terrainTypes, origin);
		PutResource(origin, gems);

		(int dx1, int dy1) = MapGenerator.Civ3SpiralOffset(1);
		(int dx2, int dy2) = MapGenerator.Civ3SpiralOffset(2);
		Tile first = At(m, origin.XCoordinate + dx1, origin.YCoordinate + dy1);
		Tile second = At(m, origin.XCoordinate + dx2, origin.YCoordinate + dy2);
		Assert.NotSame(first, second);
		MakeGrassland(terrainTypes, second);

		WorldCharacteristics wc = MakeWc(m, terrainTypes, [gems], seed: 20260214);

		// The first spiral position cannot carry this resource, so the step
		// moves on to the second.
		first.overlayTerrainType = terrainTypes[0];
		Assert.Same(second, MapGenerator.FindClumpNeighbour(wc, m, gems, origin, false));

		// Make it eligible and it is the one taken.
		MakeGrassland(terrainTypes, first);
		Assert.Same(first, MapGenerator.FindClumpNeighbour(wc, m, gems, origin, false));
	}

	// -------------------------------------------------- the shield-bonus mark

	// The predicate refuses a tile that carries the potential shield bonus. The
	// original asks the tile's slot at `+0x6c` and rejects the tile when it
	// answers yes (`0x5f3445`-`0x5f344a`); that slot is the one-bit test at
	// `0x5ea930`, kind-2 bit 16 of the tile's flag word at `+0x30`. It is not
	// the wider mask the other flags of that word might suggest - bit 29 of the
	// same word is the landmark flag (`0x5ea980`, set by `0x5ea990`) and the
	// predicate does not mind it.
	[Fact]
	public void TheCandidacyPredicateRefusesOnlyTheShieldBonusMark() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource horses = MakeResource("Horses", ResourceCategory.STRATEGIC, 160);
		Allow(terrainTypes, horses);

		GameMap m = MakeMap(20, 16, terrainTypes[0]);
		Tile centre = Centre(m);
		MakeGrassland(terrainTypes, centre);
		WorldCharacteristics wc = MakeWc(m, terrainTypes, [horses], seed: 20260214);

		// The clear side of the mask: the tile is a candidate.
		Assert.False(centre.isBonusShield);
		Assert.True(MapGenerator.CanPlaceResource(wc, m, horses, centre, true),
			"a tile without the potential shield bonus is a candidate");

		// The set side of the mask: the mark refuses it.
		centre.isBonusShield = true;
		Assert.False(MapGenerator.CanPlaceResource(wc, m, horses, centre, true),
			"the potential shield bonus mark refuses the tile");

		// The mark is the only tile flag the predicate minds. The other flags of
		// the tile record are their own fields here, and a port that widened the
		// one-bit answer into a mask over them would refuse this tile.
		centre.isBonusShield = false;
		centre.isSnowCapped = true;
		centre.isPineForest = true;
		Assert.True(MapGenerator.CanPlaceResource(wc, m, horses, centre, true),
			"the other tile flags do not reach the predicate's answer");
	}

	// This replaces `ThePotentialShieldBonusMarkDoesNotChangeResourcePlacement`,
	// which asserted that the mark played no part in this pass at all. That was
	// the reading the rule was dropped on - it argued from the generation order
	// alone and missed that the same predicate is asked again, later, on maps
	// whose tiles do carry the mark - so the test that pinned it is replaced by
	// the assertion it got wrong.
	//
	// The refusal is live where the predicate is, and the generation pass is one
	// of those places: a map whose tiles arrive marked (the BIQ import and the
	// save round-trip both set the mark) loses exactly the marked tiles as
	// candidates, and the pass places the same number of resources elsewhere.
	[Fact]
	public void TheShieldBonusMarkCostsTheMarkedTileItsResource() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource wheat = MakeResource("Wheat", ResourceCategory.BONUS, 800);
		Allow(terrainTypes, wheat);

		GameMap clean = MakeMap(40, 32, terrainTypes[11]);
		WorldCharacteristics cleanWc = MakeWc(clean, terrainTypes, [wheat], seed: 20260214);
		MapGenerator.AddResources(cleanWc, clean);

		int cleanCount = clean.tiles.Count(t => t.Resource == wheat);
		Assert.True(cleanCount > 1,
			$"the clean map must place several wheat tiles, or the refusal is untested; placed {cleanCount}");

		// Mark a tile the clean pass gave wheat to, so the mark has a placement
		// to take away, and place the same map again with that one tile marked.
		Tile target = clean.tiles.First(t => t.Resource == wheat);
		GameMap marked = MakeMap(40, 32, terrainTypes[11]);
		WorldCharacteristics markedWc = MakeWc(marked, terrainTypes, [wheat], seed: 20260214);
		Tile markedTile = marked.tileAt(target.XCoordinate, target.YCoordinate);
		markedTile.isBonusShield = true;
		MapGenerator.AddResources(markedWc, marked);

		Assert.NotEqual(wheat, markedTile.Resource);
		Assert.Equal(cleanCount, marked.tiles.Count(t => t.Resource == wheat));

		// A map whose every tile arrives marked has no candidate at all. The
		// clean run above places wheat on this very map, so an empty result here
		// is the refusal and not an empty pass.
		GameMap allMarked = MakeMap(40, 32, terrainTypes[11]);
		WorldCharacteristics allMarkedWc = MakeWc(allMarked, terrainTypes, [wheat], seed: 20260214);
		foreach (Tile t in allMarked.tiles) {
			t.isBonusShield = true;
		}
		MapGenerator.AddResources(allMarkedWc, allMarked);
		Assert.DoesNotContain(allMarked.tiles, t => t.Resource == wheat);
	}

	// The observable the mark, the refusal and the bonus-grassland pass produce
	// between them: no tile carries both a resource and the shield mark. The
	// refusal keeps a resource off a tile that arrives marked; the
	// bonus-grassland pass then skips tiles that already carry a resource, so
	// neither order can produce the pair.
	[Fact]
	public void NoTileEndsUpWithBothAResourceAndTheShieldMark() {
		List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
		Resource wheat = MakeResource("Wheat", ResourceCategory.BONUS, 1000);
		Allow(terrainTypes, wheat);

		GameMap m = MakeMap(40, 32, terrainTypes[11]);
		WorldCharacteristics wc = MakeWc(m, terrainTypes, [wheat], seed: 20260214);

		MapGenerator.AddResources(wc, m);
		MapGenerator.AddBonusGrasslands(wc, m);

		Assert.Contains(m.tiles, t => t.Resource == wheat);
		Assert.Contains(m.tiles, t => t.isBonusShield);
		Assert.DoesNotContain(m.tiles, t => t.Resource == wheat && t.isBonusShield);
	}

	// ------------------------------------------------------------- determinism

	// The pass is a pure function of the seed.
	[Fact]
	public void TheSameSeedGivesTheSameResources() {
		static GameMap Fresh() {
			List<TerrainType> terrainTypes = TerrainTypes(exoticIndex: 11);
			Resource wheat = MakeResource("Wheat", ResourceCategory.BONUS, 100);
			Resource gems = MakeResource("Gems", ResourceCategory.LUXURY, 120);
			Allow(terrainTypes, wheat);
			Allow(terrainTypes, gems);

			GameMap m = MakeMap(20, 16, terrainTypes[11]);
			WorldCharacteristics wc = MakeWc(m, terrainTypes, [wheat, gems], seed: 987654);
			MapGenerator.AddResources(wc, m);
			return m;
		}

		GameMap first = Fresh();
		GameMap again = Fresh();

		Assert.Contains("=Wheat", ResourceSignature(first));
		Assert.Equal(ResourceSignature(first), ResourceSignature(again));
	}
}
