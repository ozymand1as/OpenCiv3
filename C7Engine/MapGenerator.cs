namespace C7Engine {
	using System;
	using System.Collections.Generic;
	using System.Runtime.InteropServices;
	using System.Linq;
	using Serilog;
	using System.Diagnostics;
	using C7GameData;
	using C7GameData.Save;

	public class WorldCharacteristics {
		public enum Climate {
			Arid,
			Normal,
			Wet,
		}
		public Climate climate;

		public enum Landform {
			Archipelago,
			Continents,
			Pangaea,
		}
		public Landform landform;

		public enum OceanCoverage {
			Percent_60 = 60,
			Percent_70 = 70,
			Percent_80 = 80,
		}
		public OceanCoverage oceanCoverage;

		public enum Temperature {
			Cool,
			Temperate,
			Warm,
		}
		public Temperature temperature;

		public enum Age {
			Billion_3,
			Billion_4,
			Billion_5,
		}
		public Age age;

		public BarbarianActivity barbarianActivity;

		public int mapSeed = -1;
		public WorldSize worldSize;
		public List<TerrainType> terrainTypes;
		public List<Resource> resources = new();

		// The rules' terrain improvements. The fresh-water lake pass reads the
		// irrigation improvement's food bonus to tell which terrains the rules
		// can irrigate, which is how the original keys "lake goes on irrigable
		// ground" (re/notes/civ3_map_generator_spec.md section 2.8). An empty
		// list means the rules cannot say, and the pass degrades rather than
		// refusing everything.
		public List<SaveTerrainImprovement> terrainImprovements = new();
		public Government defaultGovernment = new();

		public int maxRankOfWorkableTiles;
		public int maxRankOfBarbarianCampTiles;

		public WorldCharacteristics() { }

		public WorldCharacteristics(SaveGame save) {
			terrainTypes = save.TerrainTypes;
			resources = save.Resources;
			terrainImprovements = save.TerrainImprovements;
			defaultGovernment = save.Governments.Find(g => g.defaultType);

			maxRankOfWorkableTiles = save.Rules.MaxRankOfWorkableTiles;
			maxRankOfBarbarianCampTiles = save.Rules.MaxRankOfBarbarianCampTiles;

			barbarianActivity = save.BarbarianInfo.barbarianActivity;
		}
	}

	// The starting-location scoring and placement live in
	// MapGeneratorStartingLocations.cs; the rest of the generator is here.
	public partial class MapGenerator {
		private static ILogger log = Log.ForContext<MapGenerator>();

		private const int MIN_TILES_PER_PLAYER_ISLAND = 81;

		// The offset this fork seeds the resource pass' own stream with. The
		// original has no such offset (see AddResources), so the value only has
		// to be stable for a seed to reproduce a map; it is named so the test
		// that pins the walk order can reproduce the shuffle.
		internal const int RESOURCE_SEED_OFFSET = 0x7171;

		// The entry point to the overall map generation process.
		public static GameMap GenerateMap(WorldCharacteristics wc) {
			if (wc.mapSeed == -1) {
				log.Information("Random seed is not specified, generating...");
				wc.mapSeed = new Random().Next(int.MaxValue);
			}
			log.Information("Seed: " + wc.mapSeed);

			// Step 0: Sanitize world characteristics
			SanitizeWorldCharacteristics(wc);

			// Step 1: generate the general shape of the terrain.
			GameMap gameMap = GenerateTerrainShape(wc);

			// Step 2: ensure we have a proper continental shelf around each
			// landmass, with land->coast->sea->ocean transitions.
			FixContinentalShelf(wc, gameMap);

			// Step 3: add hills and mountains.
			AddHillsAndMountains(wc, gameMap);

			// Step 4: use random walks to split each landmass up into potential
			// biome zones, which we will then assign in step 4 based on the
			// world characteristics.
			SplitContinentsIntoPotentialBiomes(wc, gameMap);

			// Step 5: Using the temperature information, assign each potential
			// biome region a specific biome: grassland, plains, desert, tundra
			AssignBiomes(wc, gameMap);

			// Step 6: add vegetation where appropriate.
			AddVegetation(wc, gameMap);

			// Step 6.5: seed one fresh-water lake into every landmass that is big
			// enough and has none, so that a large landmass is never left without
			// fresh water. Civ3 does this before the rivers, so that the river
			// pass and the flood-plain conversion see the finished lakes.
			AddFreshWaterLakes(wc, gameMap);

			// Step 7: Add rivers that start from high points and flow to low
			// points.
			AddRivers(wc, gameMap);

			// Step 7.5: Convert desert to flood plains. Civ3 does this after
			// rivers are placed, so a flood plain always has a river running
			// through it. Doing it here also means resources get placed on
			// flood plains using the flood plain rules (e.g. only wheat).
			AddFloodPlains(wc, gameMap);

			// Step 8: Add resources (luxury/strategic/bonus).
			AddResources(wc, gameMap);

			// Step 9: Add bonus grassland. We do this after assigning resources
			// to make sure resources get placed - we don't want to steal spots
			// for resources.
			AddBonusGrasslands(wc, gameMap);

			// Step 10: Place player starting locations
			// Placing this before barb camps so barbs don't spawn <5 tiles away from players
			// Tried placing barbs then players, but caused all players to spawn together
			DetermineStartingLocations(wc, gameMap);

			// Step 11: Add barb camps. We do this towards the end to make sure
			// that we don't have barb camps on top of resources.
			// Previously step 10
			AddBarbarianCamps(wc, gameMap);

			// Step 12: Add goody huts. This runs after the camps so that the two
			// features never share a tile, and after the starting locations so
			// that no hut lands on one.
			AddGoodyHuts(wc, gameMap);

			// Last step: Assign the terrain file and image ids to each tile so
			// we know which texture to use when displaying them.
			TerrainTextureFiles.AssignTextureDetails(new Random(wc.mapSeed + 0xebac), wc.terrainTypes, gameMap);

			return gameMap;
		}

		private static void SanitizeWorldCharacteristics(WorldCharacteristics wc) {
			// TODO: Supporting maps of odd dimensions should be doable, but beyond current tiling implementation 
			// As a mitigation, we simply shrink the map dimensions a bit 

			if (wc.worldSize.width % 2 == 1) {
				log.Warning("Uneven map width. Shrinking by one.");
				wc.worldSize.width -= 1;
			}

			if (wc.worldSize.height % 2 == 1) {
				log.Warning("Uneven map height. Shrinking by one.");
				wc.worldSize.height -= 1;
			}
		}

		private static GameMap GenerateTerrainShape(WorldCharacteristics wc) {
			int width = wc.worldSize.width;
			int height = wc.worldSize.height;
			WorldCharacteristics.OceanCoverage oceanCoverage = wc.oceanCoverage;
			WorldCharacteristics.Landform landform = wc.landform;
			int mapSeed = wc.mapSeed;

			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();

			int maxAttempts = 30;
			for (int attempt = 0; attempt < maxAttempts; ++attempt) {
				HeightMap hm = new(seed: mapSeed + 0x1234 * attempt, width:width, height:height, scale:GetNoiseScale(landform));
				GameMap m = ToLandAndWaterGameMap(wc, hm, oceanCoverage);

				if (MapIsAcceptable(wc, m)) {
					stopwatch.Stop();
					log.Information($"Map gen took {attempt} attempts and {stopwatch.ElapsedMilliseconds} milliseconds");
					return m;
				}

				if (attempt == maxAttempts - 1) {
					log.Information($"Bailing out of generating a {landform} map");
					return m;
				}
			}

			return null;
		}

		private static GameMap ToLandAndWaterGameMap(WorldCharacteristics wc, HeightMap hm, WorldCharacteristics.OceanCoverage oceanCoverage) {
			TerrainType grassland = wc.terrainTypes.Find(x => x.Key == "grassland");
			TerrainType coast = wc.terrainTypes.Find(x => x.Key == "coast");
			TerrainType sea = wc.terrainTypes.Find(x => x.Key == "sea");
			TerrainType ocean = wc.terrainTypes.Find(x => x.Key == "ocean");

			// Set up the game map with basic land and water tiles.
			GameMap m = new GameMap();
			m.numTilesTall = hm.mapHeight;
			m.numTilesWide = hm.mapWidth;
			m.wrapHorizontally = hm.wrapX;
			m.wrapVertically = hm.wrapY;
			m.optimalNumberOfCities = wc.worldSize.optimalNumberOfCities;
			m.techRate = wc.worldSize.techRate;

			ID.Factory factory = new();
			int seaLevel = hm.FindSeaLevel((int)oceanCoverage);
			int mediumWaterLevel = hm.FindSeaLevel((int)((int)oceanCoverage * .9));
			int deepWaterLevel = hm.FindSeaLevel((int)((int)oceanCoverage * .7));

			for (int Y = 0; Y < m.numTilesTall; Y++) {
				for (int X = Y % 2; X < m.numTilesWide; X += 2) {
					Tile newTile = new Tile(factory.CreateID("tile"));
					newTile.XCoordinate = X;
					newTile.YCoordinate = Y;

					int height = hm.GetHeight(X, Y);
					if (height > seaLevel) {
						newTile.baseTerrainType = grassland;
					} else if (height > mediumWaterLevel) {
						newTile.baseTerrainType = coast;
						newTile.overlayTerrainType = coast;
					} else if (height > deepWaterLevel) {
						newTile.baseTerrainType = sea;
						newTile.overlayTerrainType = sea;
					} else {
						newTile.baseTerrainType = ocean;
						newTile.overlayTerrainType = ocean;
					}
					m.tiles.Add(newTile);
				}
			}

			// Calculate neighbors and continents.
			m.computeNeighbors();
			m.recomputeContinents();

			return m;
		}

		private static double GetNoiseScale(WorldCharacteristics.Landform landform) {
			return landform switch {
				WorldCharacteristics.Landform.Archipelago => 0.2,
				WorldCharacteristics.Landform.Continents => 0.05,
				WorldCharacteristics.Landform.Pangaea => 0.02,
			};
		}

		private static bool MapIsAcceptable(WorldCharacteristics wc, GameMap m) {
			WorldCharacteristics.OceanCoverage oceanCoverage = wc.oceanCoverage;
			WorldCharacteristics.Landform landform = wc.landform;
			int totalTiles = m.tiles.Count;
			int expectedLandTiles = (int)(totalTiles * (1 - (int)oceanCoverage/100.0));

			// Count the tiles that are too close to the poles.
			int tilesInTopOrBottom10Percent = 0;
			foreach (Tile t in m.tiles) {
				if (!t.IsLand()) {
					continue;
				}
				if (t.YCoordinate <= .1 * m.numTilesTall || t.YCoordinate >= .9 * m.numTilesTall) {
					++tilesInTopOrBottom10Percent;
				}
			}

			if (landform == WorldCharacteristics.Landform.Continents) {
				// Ensure that we have at least 3 land continents, that the
				// largest isn't more than 2/3rds the land, and the second largest
				// isn't less than 1/3 the land. This should promote cases
				// where we have 2 large land masses.
				List<HashSet<Tile>> landContinents = m.continents.Where(x => x.First().IsLand()).ToList();
				if (landContinents.Count < 3) {
					return false;
				}

				if (landContinents[0].Count > 1.3 * landContinents[1].Count) {
					return false;
				}

				if (tilesInTopOrBottom10Percent > .1 * expectedLandTiles) {
					return false;
				}

				return true;
			}

			if (landform == WorldCharacteristics.Landform.Pangaea) {
				// Ensure that the largest landmass has at least 80% of the land.
				List<HashSet<Tile>> landContinents = m.continents.Where(x => x.First().IsLand()).ToList();
				if (landContinents[0].Count < .8 * expectedLandTiles) {
					return false;
				}

				// Don't allow maps where the land clumps at the north and south
				// poles.
				if (tilesInTopOrBottom10Percent > .2 * expectedLandTiles) {
					return false;
				}

				return true;
			}

			if (landform == WorldCharacteristics.Landform.Archipelago) {
				// Ensure that we have more land continents than players.
				List<HashSet<Tile>> landContinents = m.continents.Where(x => x.First().IsLand()).ToList();
				if (landContinents.Count < wc.worldSize.numberOfCivs * 1.2) {
					return false;
				}

				// Don't have any islands so large that we'd have more than 3
				// players on them.
				if (landContinents[0].Count > MIN_TILES_PER_PLAYER_ISLAND * 3) {
					return false;
				}

				// Give each player at least the minimum tiles number of . Make
				// sure that we have enough islands for that.
				int playersLeft = wc.worldSize.numberOfCivs;
				int islandsWithMultiplePlayers = 0;
				foreach (var continent in landContinents) {
					playersLeft -= continent.Count / MIN_TILES_PER_PLAYER_ISLAND;
					if (continent.Count / MIN_TILES_PER_PLAYER_ISLAND > 1) {
						++islandsWithMultiplePlayers;
					}
				}
				if (playersLeft > 0) {
					return false;
				}

				// Make sure most players are by themselves on an island.
				if (islandsWithMultiplePlayers > wc.worldSize.numberOfCivs * .4) {
					return false;
				}

				return true;
			}

			return true;
		}

		private static void AddHillsAndMountains(WorldCharacteristics wc, GameMap m) {
			// Generate a new height map that has lots of smaller blobs, to avoid
			// having one giant mountain range.
			HeightMap hm = new(seed: wc.mapSeed + 0xfeed, width:wc.worldSize.width, height:wc.worldSize.height, scale:.2);
			Random random = new Random(wc.mapSeed + 0xface);

			TerrainType hills = wc.terrainTypes.Find(x => x.Key == "hills");
			TerrainType mountains = wc.terrainTypes.Find(x => x.Key == "mountains");
			TerrainType volcano = wc.terrainTypes.Find(x => x.Key == "volcano");

			// Figure out the height bands for hills and mountains. We use bands
			// so that we can have "elevated grasslands" and so that we don't
			// get large chunks of the map just being hills/mountains, which
			// isn't useful for cities.
			int hillLowerBound, hillUpperBound;
			int mountainLowerBound, mountainUpperBound;

			// The percentage of mountains that will become volcanos.
			int volcanoPercent;

			switch (wc.age) {
				case WorldCharacteristics.Age.Billion_3:
					hillLowerBound = hm.FindSeaLevel(15);
					hillUpperBound = hm.FindSeaLevel(30);
					mountainLowerBound = hm.FindSeaLevel(60);
					mountainUpperBound = hm.FindSeaLevel(75);
					volcanoPercent = 5;
					break;
				case WorldCharacteristics.Age.Billion_4:
					hillLowerBound = hm.FindSeaLevel(20);
					hillUpperBound = hm.FindSeaLevel(30);
					mountainLowerBound = hm.FindSeaLevel(65);
					mountainUpperBound = hm.FindSeaLevel(75);
					volcanoPercent = 3;
					break;
				case WorldCharacteristics.Age.Billion_5:
					hillLowerBound = hm.FindSeaLevel(23);
					hillUpperBound = hm.FindSeaLevel(30);
					mountainLowerBound = hm.FindSeaLevel(68);
					mountainUpperBound = hm.FindSeaLevel(75);
					volcanoPercent = 1;
					break;
				default:
					throw new Exception($"Unknown age: {wc.age}");
			}

			foreach (Tile t in m.tiles) {
				if (!t.IsLand()) {
					continue;
				}

				int height = hm.GetHeight(t.XCoordinate, t.YCoordinate);
				if (height >= hillLowerBound && height < hillUpperBound) {
					t.overlayTerrainType = hills;
					continue;
				}
				if (height >= mountainLowerBound && height < mountainUpperBound) {
					if (random.Next(100) < volcanoPercent) {
						t.overlayTerrainType = volcano;
					} else {
						t.overlayTerrainType = mountains;
					}
				}
			}
		}

		// Assigns a biome id to each land tile, creating potential biome zones
		// that will later be assigned to actual biomes.
		private static void SplitContinentsIntoPotentialBiomes(WorldCharacteristics wc, GameMap m) {
			// Randomize the order we iterate through the tiles.
			Random rand = new(wc.mapSeed + 0xba11);
			List<int> tileIndicies = Enumerable.Range(0, m.tiles.Count).ToList();
			rand.Shuffle<int>(CollectionsMarshal.AsSpan(tileIndicies));

			int nextBiomeId = 0;
			foreach (int tileIndex in tileIndicies) {
				Tile t = m.tiles[tileIndex];

				// Skip tile that have been handled or tiles that already have
				// their final assignment.
				if (t.biomeRegion != -1 || !t.IsLand() || t.overlayTerrainType.isHilly()) {
					continue;
				}

				// Do a random walk to find tiles that haven't been assigned and
				// then we "bump out" in each direction from those tiles by one,
				// with some probability.
				HashSet<Tile> updated = RandomWalkAndAssignBiomeId(rand, t, walkLength:16, biomeId:nextBiomeId);
				SemiRandomFloodFillExpansion(rand, updated, nextBiomeId);
				++nextBiomeId;
			}

			// Fix up the biome regions to avoid 1 tile biomes.
			foreach (int tileIndex in tileIndicies) {
				Tile t = m.tiles[tileIndex];
				if (!t.IsLand() || t.overlayTerrainType.isHilly()) {
					continue;
				}

				Tile nw = t.neighbors[TileDirection.NORTHWEST];
				Tile sw = t.neighbors[TileDirection.SOUTHWEST];
				Tile ne = t.neighbors[TileDirection.NORTHEAST];
				Tile se = t.neighbors[TileDirection.SOUTHEAST];

				if ((t.biomeRegion == nw.biomeRegion && nw.biomeRegion != -1)
					|| (t.biomeRegion == ne.biomeRegion && ne.biomeRegion != -1)
					|| (t.biomeRegion == sw.biomeRegion && sw.biomeRegion != -1)
					|| (t.biomeRegion == se.biomeRegion && se.biomeRegion != -1)) {
					continue;
				}

				List<Tile> validNeighbors = new();
				if (nw != Tile.NONE && nw.biomeRegion != -1) validNeighbors.Add(nw);
				if (sw != Tile.NONE && sw.biomeRegion != -1) validNeighbors.Add(sw);
				if (ne != Tile.NONE && ne.biomeRegion != -1) validNeighbors.Add(ne);
				if (se != Tile.NONE && se.biomeRegion != -1) validNeighbors.Add(se);

				// This could be an island.
				if (validNeighbors.Count == 0) {
					continue;
				}

				t.biomeRegion = validNeighbors[rand.Next(validNeighbors.Count)].biomeRegion;
			}
		}

		private static HashSet<Tile> RandomWalkAndAssignBiomeId(Random rand, Tile t, int walkLength, int biomeId) {
			HashSet<Tile> result = new();

			for (int i = 0; i < walkLength; ++i) {
				if (t.biomeRegion != -1 || !t.IsLand() || t.overlayTerrainType.isHilly()) {
					continue;
				}
				t.biomeRegion = biomeId;
				result.Add(t);

				TileDirection[] options = {
					TileDirection.NORTH,
					TileDirection.SOUTH,
					TileDirection.EAST,
					TileDirection.WEST,
				};
				rand.Shuffle<TileDirection>(options);

				// Try to move in a random direction to a tile that hasn't been
				// processed.
				bool moved = false;
				foreach (TileDirection dir in options) {
					Tile neighbor = t.neighbors[dir];
					if (neighbor.biomeRegion != -1 || !neighbor.IsLand() || neighbor.overlayTerrainType.isHilly() || neighbor == Tile.NONE) {
						continue;
					}
					t = neighbor;
					moved = true;
					break;
				}

				if (!moved) {
					return result;
				}
			}
			return result;
		}

		private static void SemiRandomFloodFillExpansion(Random rand, HashSet<Tile> tiles, int biomeId) {
			List<Tile> toUpdate = new();

			foreach (Tile t in tiles) {
				foreach (Tile neighbor in t.neighbors.Values) {
					if (neighbor.biomeRegion != -1 || !neighbor.IsLand() || neighbor.overlayTerrainType.isHilly() || t == Tile.NONE) {
						continue;
					}

					if (rand.Next(100) < 50) {
						toUpdate.Add(neighbor);
					}
				}
			}

			foreach (Tile t in toUpdate) {
				t.biomeRegion = biomeId;
			}
		}

		private static void AssignBiomes(WorldCharacteristics wc, GameMap m) {
			TerrainType tundra = wc.terrainTypes.Find(x => x.Key == "tundra");
			TerrainType grassland = wc.terrainTypes.Find(x => x.Key == "grassland");
			TerrainType desert = wc.terrainTypes.Find(x => x.Key == "desert");
			TerrainType plains = wc.terrainTypes.Find(x => x.Key == "plains");

			// Like with mountains, use a very blobby scale for our temperature map.
			HeightMap hm = new(seed: wc.mapSeed + 0xdad, width:wc.worldSize.width, height:wc.worldSize.height, scale:.4);

			// Tundra also has a latitude threshold, not just a temperature
			// check, to prevent equatorial tundras.
			int tundraLatitudeThreshold;

			int plainsLowerBound, plainsUpperBound;
			int desertLowerBound, desertUpperBound;
			int tundraLowerBound, tundraUpperBound;

			// While biomes are mostly affected by temperature, give plains and
			// deserts a boost when there is less moisture.
			int aridPlainsBoost = wc.climate == WorldCharacteristics.Climate.Arid ? 8 : 0;
			int aridDesertBoost = wc.climate == WorldCharacteristics.Climate.Arid ? 5 : 0;

			switch (wc.temperature) {
				case WorldCharacteristics.Temperature.Cool:
					// About where Minneapolis is - in the last ice age glaciers
					// went lower, but we don't want all tundra.
					tundraLatitudeThreshold = 45;

					// For all the bounds here we're picking arbitrary temperature
					// bounds. So roughly 23% of the map is eligible for being
					// tundra here, though the latitude threshold also comes into
					// play later.
					tundraLowerBound = hm.FindSeaLevel(70);
					tundraUpperBound = hm.FindSeaLevel(93);

					// Roughly 15% of the map is eligible for plains, though this
					// can be boosted for arid maps.
					plainsLowerBound = hm.FindSeaLevel(15);
					plainsUpperBound = hm.FindSeaLevel(30 + aridPlainsBoost);

					// Another 15% for deserts.
					desertLowerBound = hm.FindSeaLevel(45);
					desertUpperBound = hm.FindSeaLevel(60 + aridDesertBoost);
					break;
				case WorldCharacteristics.Temperature.Temperate:
					// About where Stockholm is.
					tundraLatitudeThreshold = 59;

					tundraLowerBound = hm.FindSeaLevel(77);
					tundraUpperBound = hm.FindSeaLevel(93);

					plainsLowerBound = hm.FindSeaLevel(15);
					plainsUpperBound = hm.FindSeaLevel(35 + aridPlainsBoost);

					desertLowerBound = hm.FindSeaLevel(45);
					desertUpperBound = hm.FindSeaLevel(65 + aridDesertBoost);
					break;
				case WorldCharacteristics.Temperature.Warm:
					// About where the arctic circle is today.
					tundraLatitudeThreshold = 66;

					tundraLowerBound = hm.FindSeaLevel(83);
					tundraUpperBound = hm.FindSeaLevel(93);

					plainsLowerBound = hm.FindSeaLevel(15);
					plainsUpperBound = hm.FindSeaLevel(40 + aridPlainsBoost);

					desertLowerBound = hm.FindSeaLevel(45);
					desertUpperBound = hm.FindSeaLevel(70 + aridDesertBoost);
					break;
				default:
					throw new Exception($"Unknown temperature: {wc.temperature}");
			}

			// Randomize the order we iterate through the tiles.
			Random rand = new(wc.mapSeed + 0xba5e);
			List<int> tileIndicies = Enumerable.Range(0, m.tiles.Count).ToList();
			rand.Shuffle<int>(CollectionsMarshal.AsSpan(tileIndicies));

			foreach (int tileIndex in tileIndicies) {
				Tile t = m.tiles[tileIndex];

				// Skip water tiles and tiles that already have their biome
				// assigned.
				if (!t.IsLand() || t.overlayTerrainType != TerrainType.NONE) {
					continue;
				}

				int height = hm.GetHeight(t.XCoordinate, t.YCoordinate);

				// Figure out the latitude of this tile.
				double normalizedY = (double)t.YCoordinate / (m.numTilesTall - 1.0);
				double latitude = Math.Abs(90.0 - (normalizedY * 180.0));

				if (height >= plainsLowerBound && height < plainsUpperBound) {
					FillBiomeRegion(m, t, plains, plains);
				} else if (height >= desertLowerBound && height < desertUpperBound) {
					FillBiomeRegion(m, t, desert, desert);
				} else if (height >= tundraLowerBound && height < tundraUpperBound && latitude > tundraLatitudeThreshold) {
					FillBiomeRegion(m, t, tundra, tundra);
				} else {
					FillBiomeRegion(m, t, grassland, grassland);
				}
			}

			// Do one more pass to ensure that tundra tiles never border land
			// tiles that aren't grassland - the terrain textures don't support
			// anything else.
			foreach (Tile t in m.tiles) {
				if (t.baseTerrainType.Key != "tundra") {
					continue;
				}

				foreach (Tile neighbor in t.neighbors.Values) {
					// We can border water, other tundra, and hills/mountains
					// just fine.
					if (neighbor.baseTerrainType.Key == "tundra" || !neighbor.IsLand() || neighbor == Tile.NONE || neighbor.overlayTerrainType.isHilly()) {
						continue;
					}

					// But anything else has to be grassland.
					neighbor.baseTerrainType = grassland;
					neighbor.overlayTerrainType = grassland;
				}
			}
		}

		private static void FillBiomeRegion(GameMap m, Tile seed, TerrainType baseType, TerrainType overlayType) {
			foreach (Tile t in m.tiles) {
				if (t.biomeRegion != seed.biomeRegion) {
					continue;
				}
				t.baseTerrainType = baseType;
				t.overlayTerrainType = overlayType;
			}
		}

		private static void AddVegetation(WorldCharacteristics wc, GameMap m) {
			TerrainType forest = wc.terrainTypes.Find(x => x.Key == "forest");
			TerrainType jungle = wc.terrainTypes.Find(x => x.Key == "jungle");
			TerrainType marsh = wc.terrainTypes.Find(x => x.Key == "marsh");

			HeightMap hm = new(seed: wc.mapSeed + 0xabcde, width:wc.worldSize.width, height:wc.worldSize.height, scale:.3);

			// For jungle we also rely on latitude, not just moisture.
			int jungleLatitudeThreshold;

			int forestLowerBound, forestUpperBound, forestProbability;
			int jungleLowerBound, jungleUpperBound;
			int marshLowerBound, marshUpperBound;

			switch (wc.climate) {
				case WorldCharacteristics.Climate.Wet:
					jungleLatitudeThreshold = 30;

					forestLowerBound = hm.FindSeaLevel(60);
					forestUpperBound = hm.FindSeaLevel(93);
					forestProbability = 30;

					jungleLowerBound = hm.FindSeaLevel(15);
					jungleUpperBound = hm.FindSeaLevel(35);

					marshLowerBound = hm.FindSeaLevel(55);
					marshUpperBound = hm.FindSeaLevel(60);
					break;
				case WorldCharacteristics.Climate.Normal:
					jungleLatitudeThreshold = 23;

					forestLowerBound = hm.FindSeaLevel(65);
					forestUpperBound = hm.FindSeaLevel(93);
					forestProbability = 20;

					jungleLowerBound = hm.FindSeaLevel(15);
					jungleUpperBound = hm.FindSeaLevel(28);

					marshLowerBound = hm.FindSeaLevel(55);
					marshUpperBound = hm.FindSeaLevel(58);
					break;
				case WorldCharacteristics.Climate.Arid:
					jungleLatitudeThreshold = 20;

					forestLowerBound = hm.FindSeaLevel(70);
					forestUpperBound = hm.FindSeaLevel(93);
					forestProbability = 10;

					jungleLowerBound = hm.FindSeaLevel(15);
					jungleUpperBound = hm.FindSeaLevel(20);

					marshLowerBound = hm.FindSeaLevel(55);
					marshUpperBound = hm.FindSeaLevel(56);
					break;
				default:
					throw new Exception($"Unknown climate: {wc.climate}");
			}

			Random rand = new(wc.mapSeed + 0xe5ab);
			foreach (Tile t in m.tiles) {
				// Skip water tiles and tiles that are hilly.
				if (!t.IsLand() || t.overlayTerrainType.isHilly()) {
					continue;
				}

				int height = hm.GetHeight(t.XCoordinate, t.YCoordinate);

				// Figure out the latitude of this tile.
				double normalizedY = (double)t.YCoordinate / (m.numTilesTall - 1.0);
				double latitude = Math.Abs(90.0 - (normalizedY * 180.0));

				if (height >= forestLowerBound && height < forestUpperBound && rand.Next(100) < forestProbability) {
					// Forests can go on any terrain type.
					t.overlayTerrainType = forest;
				} else if (height >= jungleLowerBound && height < jungleUpperBound
							&& latitude < jungleLatitudeThreshold && t.overlayTerrainType.Key == "grassland") {
					// We only put jungle on grassland.
					t.overlayTerrainType = jungle;
				} else if (height >= marshLowerBound && height < marshUpperBound && t.overlayTerrainType.Key == "grassland") {
					// We only put marsh on grassland.
					t.overlayTerrainType = marsh;
				}
			}
		}

		// The original generator seeds one fresh-water lake into every landmass
		// that is bigger than 74 tiles and has no fresh water of its own, so that
		// no large landmass is left without a fresh-water source. The rules, the
		// addresses they were read from and what was verified against the binary
		// are written up in re/notes/civ3_map_generator_spec.md section 2.8.

		// A body is only considered when its tile count is *greater* than this;
		// the original compares the body's area against 0x4b = 75 at 0x5ed6b7,
		// so 74 is the largest body left alone.
		internal const int LAKE_BODY_MIN_AREA = 74;

		// A water body this size or smaller counts as fresh water. The original
		// tests a body's area against 0x14 = 20 at 0x5f39a4, both in the lake
		// pass's "does this landmass already have fresh water" scan and in the
		// game's fresh-water query that the city growth rules call.
		internal const int FRESH_WATER_MAX_AREA = 20;

		// The pass draws on its own stream, seeded from the world seed plus this
		// offset (0x5ed5fb adds 0x7c0 to the seed field at world+0x1ec). It is
		// not the river pass's stream, which uses a different offset, so a seed
		// still reproduces a map exactly but the two passes do not share draws.
		private const int LAKE_SEED_OFFSET = 0x7c0;

		/// <summary>
		/// Seeds one fresh-water lake into every landmass that is big enough and
		/// has none, and returns how many lakes it dug.
		/// </summary>
		internal static int AddFreshWaterLakes(WorldCharacteristics wc, GameMap m) {
			int tileCount = m.tiles.Count;
			if (tileCount == 0) {
				return 0;
			}

			TerrainType coast = wc.terrainTypes.Find(x => x.Key == "coast");
			if (coast == null) {
				log.Warning("Rules have no 'coast' terrain; skipping fresh-water lake generation.");
				return 0;
			}

			// The bodies are the connected components the generator has already
			// labelled; every tile of a body carries the same id, land and water
			// alike.
			int bodyCount = m.continents.Count;
			int[] bodyArea = new int[bodyCount];
			foreach (Tile t in m.tiles) {
				++bodyArea[t.continent];
			}

			// The shuffle is seeded, so a seed reproduces the same lakes.
			Random rand = new(wc.mapSeed + LAKE_SEED_OFFSET);
			List<int> tileIndices = Enumerable.Range(0, tileCount).ToList();
			rand.Shuffle<int>(CollectionsMarshal.AsSpan(tileIndices));

			SaveTerrainImprovement irrigation = wc.terrainImprovements?.Find(x => x.key == Tile.TileOverlays.IRRIGATION);

			int lakes = 0;
			for (int body = 0; body < bodyCount; ++body) {
				// Small bodies are left alone.
				if (bodyArea[body] <= LAKE_BODY_MIN_AREA) {
					continue;
				}

				// A landmass that already has fresh water is left alone too.
				if (BodyHasFreshWater(m, body, bodyArea, irrigation)) {
					continue;
				}

				// First the strict search: the tile and all eight of its neighbours
				// have to be land the rules can irrigate. If the landmass has no
				// such spot - an all-mountain island, say - a second walk of the
				// same shuffled order only asks for no water next door.
				if (SeedLake(m, coast, tileIndices, body, irrigation, strict: true)
					|| SeedLake(m, coast, tileIndices, body, irrigation, strict: false)) {
					++lakes;
				}
			}

			// Carving the lakes changes the bodies, and the fresh-water flag the
			// city rules read is worked out from a water body's size, so the
			// bodies are worked out again now that they are final. The original
			// does the same thing at 0x5edb51, where it calls the map's
			// continent-identifying method before it returns.
			m.recomputeContinents();

			return lakes;
		}

		// Whether a body already has fresh water: a tile of the body that borders
		// a lake or carries a river, on terrain the rules can irrigate. Read at
		// 0x5ed6c1-0x5ed78c. Note the irrigation test is on the tile itself, so a
		// landmass whose only small water body sits in the mountains is still
		// given its own lake.
		private static bool BodyHasFreshWater(GameMap m, int body, int[] bodyArea, SaveTerrainImprovement irrigation) {
			foreach (Tile t in m.tiles) {
				if (t.continent == body && HasFreshWater(t, bodyArea) && IsIrrigable(t, irrigation)) {
					return true;
				}
			}

			return false;
		}

		// The game's own fresh-water query (world vtable slot 0x60, 0x5f39e0):
		// the tile touches fresh water, or it carries a river. The river half is
		// only ever true when the pass runs after the river pass; in the driver's
		// order it is not, and the query is the neighbourhood one.
		private static bool HasFreshWater(Tile t, int[] bodyArea) {
			return BordersFreshWater(t, bodyArea) || t.BordersRiver();
		}

		// Whether the tile or one of its eight neighbours is a water tile of a
		// body small enough to be fresh water. This is the original's
		// "is near lake" test (0x5f38c0): it walks the tile and its eight
		// neighbours, and a position only counts when it is inside the map, is
		// water, and its body's area is 20 or less.
		private static bool BordersFreshWater(Tile t, int[] bodyArea) {
			foreach (Tile n in Neighbourhood(t)) {
				if (n.IsLand() || n.continent >= bodyArea.Length) {
					continue;
				}

				if (bodyArea[n.continent] <= FRESH_WATER_MAX_AREA) {
					return true;
				}
			}

			return false;
		}

		// The tile itself, then its eight neighbours. A position that falls off
		// the map is not in the neighbourhood at all, which is what the original
		// does with its out-of-bounds test.
		private static IEnumerable<Tile> Neighbourhood(Tile t) {
			yield return t;

			foreach (Tile n in t.neighbors.Values) {
				if (n != Tile.NONE) {
					yield return n;
				}
			}
		}

		// Walks the shuffled tile order once looking for a tile of this body to
		// turn into fresh water (0x5ed7c2-0x5ed9b2 and 0x5ed9ce-0x5edb12). With
		// `strict` the tile and its eight neighbours all have to be irrigable
		// land; without it only water is refused.
		private static bool SeedLake(GameMap m, TerrainType coast, List<int> tileIndices, int body,
			SaveTerrainImprovement irrigation, bool strict) {
			foreach (int index in tileIndices) {
				Tile t = m.tiles[index];
				if (t.continent != body) {
					continue;
				}

				// A tile that already carries a river is never chosen. In the
				// driver's order the rivers are laid after this pass, so no tile
				// carries one yet; the test is kept because it is the original's
				// (0x5ed823).
				if (t.BordersRiver()) {
					continue;
				}

				if (NeighbourhoodIsDryLand(t, irrigation, strict)) {
					// The lake is one tile, and it is written as coast. Setting the
					// real terrain as well as the underlying one is what the
					// original's terrain setter does at 0x5e99c8, so a lake that
					// lands on forest or hills is water, not watered-over land.
					t.baseTerrainType = coast;
					t.overlayTerrainType = coast;
					return true;
				}
			}

			return false;
		}

		// Whether the whole neighbourhood passes the seed test: land, and - on the
		// strict walk - land the rules can irrigate.
		private static bool NeighbourhoodIsDryLand(Tile t, SaveTerrainImprovement irrigation, bool strict) {
			foreach (Tile n in Neighbourhood(t)) {
				if (!n.IsLand()) {
					return false;
				}

				if (strict && !IsIrrigable(n, irrigation)) {
					return false;
				}
			}

			return true;
		}

		// Whether the rules can irrigate a tile's terrain. The original tests the
		// terrain's irrigation bonus (0x5dbe70, the terrain record field at
		// +0x4c, or +0x94 when the tile is a landmark) and the pass is really
		// asking "is this irrigable ground". Both rule paths carry that value:
		// the BIQ import turns each terrain's IrrigationBonus into the irrigation
		// improvement's food bonus for it (ImportCiv3's terrain improvement
		// bonuses), and ruleset.json lists the same four terrains under
		// terrainImprovements.irrigation.bonusYields.
		private static bool IsIrrigable(Tile t, SaveTerrainImprovement irrigation) {
			if (irrigation == null) {
				// A ruleset with no irrigation improvement cannot say which
				// terrains are irrigable; the strict search then degenerates into
				// the relaxed one rather than refusing every tile.
				return true;
			}

			return irrigation.bonusYields.TryGetValue(t.overlayTerrainType?.Key, out var yields)
				&& yields.TryGetValue(Tile.YieldType.Food, out int food) && food > 0;
		}

		// The original river pass marks river *edges*, not river tiles: a tile
		// carries one bit per edge and the river is recorded on both of the
		// tiles that share the edge. Everything below follows that pass; the
		// rules and the addresses they were read from are written up in
		// re/notes/civ3_map_generator_spec.md section 2.9.

		// A body this size or smaller does not earn its own river out of the
		// budget; it can still be given one by the share that is proportional to
		// area.
		private const int RIVER_BODY_MIN_AREA = 36;

		// The budget is a number of rivers the pass may start, shared out
		// between the bodies; it is not a cap on the number of edges a river
		// lays down, which the 21-step limit on a run bounds instead.
		private const int RIVER_BUDGET_SCALE = 75;
		private const int RIVER_BUDGET_DIVISOR = 5000;
		private const int RIVER_BUDGET_CAP = 256;

		// At most this many sources are kept, and no two of them are closer
		// than this many tiles.
		private const int RIVER_SOURCE_SLOTS = 256;
		private const int RIVER_SOURCE_SPACING = 3;

		// A river run is capped at 21 steps.
		private const int RIVER_MAX_STEPS = 21;

		private const int RIVER_RANK_SPIRAL_SIZE = 121;

		// The score of a continuation is the average of two samples, one on each
		// side of the direction the step would take, and each sample is the 49
		// positions of a three-ring spiral (rule 2.9.17).
		private const int RIVER_SAMPLE_SPIRAL_SIZE = 49;

		// How far along the candidate direction the two sampling centres sit from
		// the tile the river is on.
		private const int RIVER_SAMPLE_LEAD = 4;

		// A sampled position that is off the map or under water is worth this
		// much to the score.
		private const int RIVER_SAMPLE_OFF_MAP = -10;

		// The four positions the pass probes around a candidate source, as raw
		// (x, y) offsets from the tile: the tile itself, the two tiles one row
		// below it, and the tile two rows below it.
		private static readonly (int dx, int dy)[] riverProbeOffsets = {
			(0, 0), (-1, 1), (1, 1), (0, 2),
		};

		// The 49 positions the score's sampler walks around a centre, in the
		// order the pass numbers them: the centre itself, the eight positions of
		// the first ring, the sixteen of the second and the twenty-four of the
		// third.
		private static readonly (int dx, int dy)[] riverSampleOffsets = {
			(0, 0),
			(1, -1), (2, 0), (1, 1), (0, 2), (-1, 1), (-2, 0), (-1, -1), (0, -2),
			(1, -3), (2, -2), (3, -1), (3, 1), (2, 2), (1, 3), (-1, 3), (-2, 2),
			(-3, 1), (-3, -1), (-2, -2), (-1, -3), (0, -4), (4, 0), (0, 4), (-4, 0),
			(1, -5), (2, -4), (3, -3), (4, -2), (5, -1), (5, 1), (4, 2), (3, 3),
			(2, 4), (1, 5), (-1, 5), (-2, 4), (-3, 3), (-4, 2), (-5, 1), (-5, -1),
			(-4, -2), (-3, -3), (-2, -4), (-1, -5), (0, -6), (6, 0), (0, 6), (-6, 0),
		};

		// One river per 750 tiles, capped at 256.
		internal static int RiverBudgetFor(int tileCount) {
			return Math.Min(tileCount * RIVER_BUDGET_SCALE / RIVER_BUDGET_DIVISOR, RIVER_BUDGET_CAP);
		}

		// How many rivers each body may start. A body gets one as soon as it is
		// bigger than 36 tiles - that is what the threshold is for, not a
		// minimum size for carrying a river - and what is left of the budget is
		// shared out in proportion to area, walking from the last body back to
		// the second; the first body takes the remainder. The caller turns these
		// counts into the cumulative slot ranges the pass indexes.
		internal static int[] AllocateRiverSlots(int[] bodyArea, int tileCount) {
			int budget = RiverBudgetFor(tileCount);
			int[] slots = new int[bodyArea.Length];

			for (int i = 0; i < bodyArea.Length; ++i) {
				if (bodyArea[i] > RIVER_BODY_MIN_AREA) {
					slots[i] = 1;
					--budget;
				}
			}

			int remaining = Math.Max(budget, 0);
			int remainingArea = 0;
			foreach (int area in bodyArea) {
				remainingArea += area;
			}

			for (int i = bodyArea.Length - 1; i >= 1 && remainingArea > 0; --i) {
				int share = bodyArea[i] * remaining / remainingArea;
				slots[i] += share;
				remaining -= share;
				remainingArea -= bodyArea[i];
			}
			if (bodyArea.Length > 0) {
				slots[0] += remaining;
			}

			return slots;
		}

		// The pass itself, split out from GenerateMap so that tests can run it
		// on a map they built by hand.
		/// <summary>
		/// Marks the river edges of a generated map and returns how many rivers
		/// it started, which the budget caps.
		/// </summary>
		internal static int AddRivers(WorldCharacteristics wc, GameMap m) {
			int tileCount = m.tiles.Count;
			if (tileCount == 0) {
				return 0;
			}

			Random rand = new(wc.mapSeed + 0xabc12);

			// The bodies are the connected components the generator has already
			// labelled; every tile of a body carries the same id.
			int bodyCount = m.continents.Count;
			int[] bodyArea = new int[bodyCount];
			foreach (Tile t in m.tiles) {
				++bodyArea[t.continent];
			}

			int[] slots = AllocateRiverSlots(bodyArea, tileCount);

			// Prefix sums turn slots[i] into the end of body i's run of source
			// slots and slots[i - 1] into its start. The original stops at the
			// first body with nothing, which leaves the bodies after it holding
			// the raw count rather than a running total.
			for (int i = 1; i < bodyCount; ++i) {
				if (slots[i] == 0) {
					break;
				}
				slots[i] += slots[i - 1];
			}

			// Phase one: walk a shuffled tile list and keep, for each body, the
			// best-ranked sources. Slot 0 doubles as the "empty" marker, just as
			// it does in the original, so tile 0 can never be a source.
			int[] sourceRank = new int[RIVER_SOURCE_SLOTS];
			int[] sourceTile = new int[RIVER_SOURCE_SLOTS];

			List<int> tileIndices = Enumerable.Range(0, tileCount).ToList();
			rand.Shuffle<int>(CollectionsMarshal.AsSpan(tileIndices));

			foreach (int index in tileIndices) {
				Tile t = m.tiles[index];
				int start = t.continent == 0 ? 0 : slots[t.continent - 1];
				int end = slots[t.continent];
				if (end == 0) {
					continue;
				}

				if (!IsRiverSourceCandidate(m, t, out bool[] water)) {
					continue;
				}

				int rank = RiverSourceRank(m, t);

				int at = start;
				while (at < end && rank <= sourceRank[at]) {
					++at;
				}
				if (at >= end) {
					continue;
				}

				bool tooClose = false;
				for (int i = start; i < end; ++i) {
					if (WrappedTileDistance(m, t, m.tiles[sourceTile[i]]) <= RIVER_SOURCE_SPACING) {
						tooClose = true;
						break;
					}
				}
				if (tooClose) {
					continue;
				}

				for (int i = end - 1; i > at; --i) {
					sourceRank[i] = sourceRank[i - 1];
					sourceTile[i] = sourceTile[i - 1];
				}
				sourceRank[at] = rank;
				sourceTile[at] = index;
			}

			// Phase two: trace a river from every source that survived.
			int riversStarted = 0;
			for (int i = 0; i < RIVER_SOURCE_SLOTS; ++i) {
				if (sourceTile[i] == 0) {
					continue;
				}

				Tile source = m.tiles[sourceTile[i]];
				TileDirection dir = RiverSourceDirection(m, source);
				if (dir == TileDirection.INVALID) {
					continue;
				}

				Tile start = RiverSourceStart(m, source, dir);
				if (start == Tile.NONE) {
					continue;
				}

				List<(Tile tile, TileDirection dir)> path = new();
				HashSet<Tile> visited = new() { start };
				if (GrowRiver(m, start, dir, 0, 0, 0, rand, visited, path)) {
					++riversStarted;
					foreach ((Tile tile, TileDirection step) in path) {
						MarkRiverStep(tile, step);
					}
				}
			}

			return riversStarted;
		}

		// A tile can start a river when exactly two of the four probes are water
		// and the two ends of the probed run disagree.
		private static bool IsRiverSourceCandidate(GameMap m, Tile t, out bool[] water) {
			water = new bool[4];
			int waterCount = 0;

			for (int i = 0; i < riverProbeOffsets.Length; ++i) {
				Tile probe = m.tileAt(t.XCoordinate + riverProbeOffsets[i].dx, t.YCoordinate + riverProbeOffsets[i].dy);
				water[i] = probe != Tile.NONE && !probe.IsLand();
				if (water[i]) {
					++waterCount;
				}
			}

			return waterCount == 2 && water[0] != water[3];
		}

		// The rank a source is kept by: a sum over the 121 tiles within five
		// rings of it, weighting nearby tiles more heavily, jungle and marsh
		// above other terrain, and flood plains not at all.
		private static int RiverSourceRank(GameMap m, Tile source) {
			int rank = 0;

			for (int i = 0; i < RIVER_RANK_SPIRAL_SIZE; ++i) {
				(int dx, int dy) = RiverSpiralOffset(i);
				Tile t = m.tileAt(source.XCoordinate + dx, source.YCoordinate + dy);
				if (t == Tile.NONE || !t.IsLand() || t.continent != source.continent) {
					continue;
				}

				rank += RiverTerrainRank(t, i);
			}

			return rank;
		}

		private static int RiverTerrainRank(Tile t, int spiralIndex) {
			// The original weights by Civ3's terrain id: flood plain contributes
			// nothing, jungle and marsh more than anything else. Water never gets
			// here, because the body test rejects it first.
			switch (TerrainType.Civ3TerrainIdForKey(t.overlayTerrainType.Key)) {
				case 4:
					return 0;
				case 8:
					return RiverRingRank(spiralIndex, 16, 32, 64);
				case 9:
					return RiverRingRank(spiralIndex, 32, 64, 64);
				default:
					return RiverRingRank(spiralIndex, 8, 16, 32);
			}
		}

		// A tile counts for more the closer it is to the source, and the three
		// bands are the first three rings of the spiral.
		private static int RiverRingRank(int spiralIndex, int inner, int middle, int centre) {
			int rank = 0;
			if (spiralIndex < 25) {
				rank += inner;
			}
			if (spiralIndex < 9) {
				rank += middle;
			}
			if (spiralIndex < 1) {
				rank += centre;
			}
			return rank;
		}

		// Ring-by-ring enumeration of the tiles around a point, using the eight
		// directions the original numbers.
		private static (int dx, int dy) RiverSpiralOffset(int index) {
			if (index <= 0) {
				return (0, 0);
			}

			int ring = 1;
			int first = 1;
			while (index >= first + 8 * ring) {
				first += 8 * ring;
				++ring;
			}

			(int ux, int uy) = ((index - first) / ring) switch {
				0 => (1, -1),
				1 => (2, 0),
				2 => (1, 1),
				3 => (0, 2),
				4 => (-1, 1),
				5 => (-2, 0),
				6 => (-1, -1),
				_ => (0, -2),
			};
			return (ux * ring, uy * ring);
		}

		// Distance between two tiles, halved, with the world wrapping taken into
		// account.
		private static int WrappedTileDistance(GameMap m, Tile a, Tile b) {
			int dx = Math.Abs(m.CalculateXDelta(a.XCoordinate, b.XCoordinate));
			int dy = Math.Abs(m.CalculateYDelta(a.YCoordinate, b.YCoordinate));
			return (dx + dy) / 2;
		}

		// The direction a river leaves its source in, read off the water pattern
		// at the first two probes. The four directions are the four diagonals:
		// north-east, south-east, south-west and north-west.
		internal static TileDirection RiverSourceDirection(GameMap m, Tile t) {
			if (!IsRiverSourceCandidate(m, t, out bool[] water)) {
				return TileDirection.INVALID;
			}

			return (water[0], water[1]) switch {
				(false, false) => TileDirection.NORTHWEST,
				(false, true) => TileDirection.NORTHEAST,
				(true, false) => TileDirection.SOUTHWEST,
				_ => TileDirection.SOUTHEAST,
			};
		}

		// The start point the tracer is handed, read at `0x5f107d`. The probes
		// are read a second time; the water at the first two picks the diagonal
		// (rule 2.9.14), and the point handed to the tracer is the sum of the
		// positions of the probes that were *not* water. A probe that falls off
		// the map is not water - it still counts as land when the diagonal is
		// picked - but its position is not added to the sum, so a source with
		// only one probe on the map is handed a point at half its own
		// coordinates. The tracer's own coordinates are doubled relative to the
		// tile grid, and the pass turns one of them back into a tile with the
		// perpendicular of the step, halving with the usual truncation toward
		// zero. Its tile lookup then folds the result onto the storage grid
		// rather than refusing it, and this grid holds the same tiles in the
		// same order, so the fold is the same index arithmetic.
		internal static Tile RiverSourceStart(GameMap m, Tile t, TileDirection dir) {
			int sumX = 0;
			int sumY = 0;

			for (int i = 0; i < riverProbeOffsets.Length; ++i) {
				Tile probe = m.tileAt(t.XCoordinate + riverProbeOffsets[i].dx, t.YCoordinate + riverProbeOffsets[i].dy);
				if (probe == Tile.NONE || !probe.IsLand()) {
					continue;
				}
				sumX += probe.XCoordinate;
				sumY += probe.YCoordinate;
			}

			(int px, int py) = RiverDirectionOffset(RiverPerpendicular(dir));
			int x = (sumX - px) / 2;
			int y = (sumY - py) / 2;
			if (x < 0 || y < 0 || x >= m.numTilesWide || y >= m.numTilesTall) {
				return Tile.NONE;
			}
			return m.tiles[m.tileCoordsToIndex(x, y)];
		}

		// Walks a river on from `t` in `dir`. The walk looks at three
		// continuations - straight on and a quarter turn either way - refuses
		// any that is water, that already carries a river, or that would run
		// alongside the river just laid, and takes the best one. Past the
		// second step it also takes a second, and sometimes a third,
		// continuation, which is how rivers branch. A run is capped at 21
		// steps, and a step whose whole continuation fails is rolled back so
		// that a river never ends in a stub.
		private static bool GrowRiver(GameMap m, Tile t, TileDirection dir, int steps, int straight, int lateral,
			Random rand, HashSet<Tile> visited, List<(Tile tile, TileDirection dir)> path) {
			if (steps >= RIVER_MAX_STEPS) {
				return true;
			}

			// The three continuations the pass offers, each tagged with which of
			// the three it is: 0 a quarter turn counter-clockwise, 1 straight on,
			// 2 a quarter turn clockwise. `straight` and `lateral` are the run
			// counters the score's divisor reads.
			(TileDirection dir, int turn)[] options = {
				(dir, 1),
				(dir.RotatedCounterClockwise90Degrees(), 0),
				(RiverTurnClockwise(dir), 2),
			};

			List<(TileDirection dir, Tile tile, int score, int turn)> usable = new();
			foreach ((TileDirection d, int turn) in options) {
				if (d == TileDirection.INVALID || !t.neighbors.TryGetValue(d, out Tile n)) {
					continue;
				}
				if (n == Tile.NONE || !n.IsLand() || n.BordersRiver() || visited.Contains(n)) {
					continue;
				}
				bool runsAlongside = false;
				foreach (Tile x in n.neighbors.Values) {
					if (x != Tile.NONE && x != t && (x.BordersRiver() || visited.Contains(x))) {
						runsAlongside = true;
						break;
					}
				}
				if (runsAlongside) {
					continue;
				}
				if (RiverContinuationIsRefused(turn, lateral)) {
					// The pass refuses this continuation outright and never samples
					// the terrain for it, so it draws no random numbers here
					// (rule 2.9.17.1).
					usable.Add((d, n, RiverRefusedScore, turn));
					continue;
				}
				int sum = RiverSampleSum(m, t, d, steps, rand);
				int divisor = RiverScoreDivisor(turn, straight, lateral);
				usable.Add((d, n, divisor < 1 ? sum : sum / divisor, turn));
			}

			if (!RiverContinuationPlan(steps, usable.Select(x => x.score).ToList(), rand,
					out List<int> order, out int branches)) {
				return false;
			}

			bool grew = false;
			for (int i = 0; i < branches; ++i) {
				(TileDirection d, Tile n, int _, int turn) = usable[order[i]];
				int nextStraight = turn == 1 ? straight + 1 : 0;
				int nextLateral = turn == 0 ? lateral - 1 : (turn == 2 ? lateral + 1 : lateral);
				visited.Add(n);
				path.Add((t, d));
				if (GrowRiver(m, n, d, steps + 1, nextStraight, nextLateral, rand, visited, path)) {
					grew = true;
				} else {
					path.RemoveAt(path.Count - 1);
					visited.Remove(n);
				}
			}

			return grew;
		}

		// A river direction a quarter turn clockwise from `dir`.
		private static TileDirection RiverTurnClockwise(TileDirection dir) {
			return dir switch {
				TileDirection.NORTHEAST => TileDirection.SOUTHEAST,
				TileDirection.SOUTHEAST => TileDirection.SOUTHWEST,
				TileDirection.SOUTHWEST => TileDirection.NORTHWEST,
				TileDirection.NORTHWEST => TileDirection.NORTHEAST,
				_ => TileDirection.INVALID,
			};
		}

		// The order the pass considers the three continuations in, and how many
		// of them it takes. The port offers them in its own array order - straight
		// on, a quarter turn counter-clockwise, a quarter turn clockwise - and
		// ranks them best first with a stable sort. The original samples and arrays
		// the same three in a DIFFERENT order: counter-clockwise, straight on,
		// clockwise (`0x5f0182`, `0x5f01bf`, `0x5f020d`), received by its ranker in
		// that order at `0x5f0308`. So an exact tie between two continuations can
		// resolve the other way here; only ties are affected, because the scores
		// themselves are compared by value. Recorded in the spec's port summary.
		// A continuation the
		// pass refuses outright (rule 2.9.17.1) scores the refusal, so it can
		// never lead the step, and a step whose best score is the refusal leads
		// nowhere; a continuation whose score is not above the refusal earns no
		// second or third branch and draws no random number for one. Otherwise the
		// best is always taken, and from the third step of a run onwards a second
		// continuation is taken as well, and sometimes a third, the choice
		// depending on how far behind the second (and third) score trails the best
		// and on a random draw.
		//
		// A sampled score that is negative without being the refusal is still a
		// candidate here. The binary instead ends a step whose best score is
		// below zero, and when the run is old enough it sets the terrain of a
		// folded position to mountains rather than taking the step back (read at
		// `0x5f021e` and `0x5f023a`). That branch is not part of the port's model,
		// so the port keeps such a step alive.
		internal static bool RiverContinuationPlan(int steps, IReadOnlyList<int> scores, Random rand,
			out List<int> order, out int branches) {
			order = Enumerable.Range(0, scores.Count)
				.OrderByDescending(i => scores[i])
				.ToList();
			branches = 0;
			if (order.Count == 0 || scores[order[0]] == RiverRefusedScore) {
				return false;
			}

			branches = 1;
			if (steps > 1 && order.Count > 1) {
				int best = scores[order[0]];
				int gap = best - scores[order[1]];
				if (best / 8 < gap) {
					if (scores[order[1]] > RiverRefusedScore) {
						branches = 1 + rand.Next(2);
					}
				} else {
					branches = 2;
					if (order.Count > 2 && scores[order[2]] > RiverRefusedScore) {
						branches = 2 + rand.Next(2);
					}
				}
			}
			branches = Math.Min(branches, order.Count);
			return true;
		}

		// The score the pass gives a continuation it refuses outright, read at
		// `0x5f0141` and `0x5f01d1`: the continuation is never sampled, and a step
		// whose best score is this leads nowhere.
		internal const int RiverRefusedScore = -1;

		// Whether the pass refuses one of the three continuations outright. Read
		// at `0x5f0141` and `0x5f01d1`: a quarter turn counter-clockwise (the
		// `dir + 3` continuation, turn 0) is refused once the lateral counter has
		// run two turns that way, and a quarter turn clockwise (the `dir + 1`
		// continuation, turn 2) once it has run two turns the other way. A
		// straight step is never refused.
		internal static bool RiverContinuationIsRefused(int turn, int lateral) {
			return turn switch {
				0 => lateral < -1,
				2 => lateral >= 2,
				_ => false,
			};
		}

		// The sum of the two samples the score is built from, read at `0x5efa90`
		// sampling through `0x5ef880`: two samples along the direction, one on
		// each side of it, averaged. The two centres sit four steps along the
		// direction from the tile the river is on, the second one a step to the
		// side of it; each contributes the 49 positions of the spiral, and a
		// position off the map or under water is worth -10. The pass adds the two
		// centres together index by index, so for each position of the spiral it
		// reads the first centre's value and then the second centre's value for
		// that same position before moving on.
		private static int RiverSampleSum(GameMap m, Tile t, TileDirection dir, int steps, Random rand) {
			(int dx, int dy) = RiverDirectionOffset(dir);
			(int px, int py) = RiverDirectionOffset(RiverPerpendicular(dir));

			int centreX = t.XCoordinate + RIVER_SAMPLE_LEAD * dx;
			int centreY = t.YCoordinate + RIVER_SAMPLE_LEAD * dy;
			int sum = 0;
			for (int i = 0; i < RIVER_SAMPLE_SPIRAL_SIZE; ++i) {
				(int ox, int oy) = riverSampleOffsets[i];
				sum += RiverSampleValue(m, centreX + ox, centreY + oy, steps, i + 1, rand);
				sum += RiverSampleValue(m, centreX + px + ox, centreY + py + oy, steps, i + 1, rand);
			}

			return sum;
		}

		// The divisor the score is scaled by before the three continuations are
		// compared, read at `0x5f015c` for the counter-clockwise turn and at
		// `0x5f01e7` for the clockwise one. A sideways step is halved only while
		// the lateral counter is already on the side the turn is taking the
		// river: the counter falls with every counter-clockwise turn and rises
		// with every clockwise one, so a counter-clockwise step is halved when it
		// is below zero and a clockwise step when it is above zero. A step that
		// goes straight on is not divided at all for its first two steps and then
		// halved more and more as the run gets longer, which is what turns a
		// river rather than letting it run straight across the map.
		internal static int RiverScoreDivisor(int turn, int straight, int lateral) {
			switch (turn) {
				case 1:
					return straight < 2 ? 0 : 1 << (straight - 1);
				case 0:
					return lateral < 0 ? 2 : 0;
				default:
					return lateral <= 0 ? 0 : 2;
			}
		}

		// The value the sampler reads at one position, read at `0x5ef880`:
		// off the map or under water is -10, and anything else is the terrain's
		// base value for the step the river has reached, doubled for the second
		// position of the spiral and doubled again for the centre, plus two
		// five-point random terms.
		private static int RiverSampleValue(GameMap m, int x, int y, int steps, int spiralIndex, Random rand) {
			Tile t = m.tileAt(x, y);
			if (t == Tile.NONE || !t.IsLand()) {
				return RIVER_SAMPLE_OFF_MAP;
			}

			int value = RiverSampleBase(t, steps) - 5 + rand.Next(11);
			if (spiralIndex <= 2) {
				value *= 2;
			}
			if (spiralIndex <= 1) {
				value *= 2;
			}
			return value - 5 + rand.Next(11);
		}

		// The terrain's base value for the step the river has reached. Hills
		// and mountains are poor ground while the river is young and good
		// ground once it is long; jungle and marsh are always good, and flood
		// plains are worth nothing because a river has already been there.
		private static int RiverSampleBase(Tile t, int steps) {
			switch (TerrainType.Civ3TerrainIdForKey(t.overlayTerrainType.Key)) {
				case 0:
					return steps < 4 ? 2 : (steps < 8 ? 0 : -1);
				case 1:
					return steps < 4 ? 4 : (steps < 8 ? 2 : -1);
				case 2:
					return steps < 4 ? 5 : (steps < 8 ? 2 : -1);
				case 5:
					return steps < 4 ? -3 : 5;
				case 6:
					return steps < 4 ? -3 : (steps < 8 ? 10 : 15);
				case 7:
					return steps < 4 ? 4 : (steps < 8 ? 2 : -1);
				case 8:
					return steps < 4 ? 4 : (steps < 8 ? 2 : -1);
				case 9:
					return steps < 4 ? 4 : (steps < 8 ? 2 : -1);
				case 10:
					return -5;
				default:
					return steps < 4 ? 0 : (steps < 8 ? 0 : -1);
			}
		}

		// The (x, y) step of a diagonal direction, in the pass's own numbering:
		// north-east, south-east, south-west and north-west.
		private static (int dx, int dy) RiverDirectionOffset(TileDirection dir) {
			return dir switch {
				TileDirection.NORTHEAST => (1, -1),
				TileDirection.SOUTHEAST => (1, 1),
				TileDirection.SOUTHWEST => (-1, 1),
				TileDirection.NORTHWEST => (-1, -1),
				_ => (0, 0),
			};
		}

		// The direction the pass pairs with a diagonal: the same step with its
		// row component flipped. The pass uses it to turn a doubled position
		// back into the tile the position stands on.
		private static TileDirection RiverPerpendicular(TileDirection dir) {
			return dir switch {
				TileDirection.NORTHEAST => TileDirection.SOUTHEAST,
				TileDirection.SOUTHEAST => TileDirection.NORTHEAST,
				TileDirection.SOUTHWEST => TileDirection.NORTHWEST,
				TileDirection.NORTHWEST => TileDirection.SOUTHWEST,
				_ => TileDirection.INVALID,
			};
		}

		// Records the river step that runs from `tile` to its neighbour in
		// `dir`. The original marks both bits of the step's diagonal on both of
		// the tiles it passes, and the reader that turns an edge into a river - 
		// Tile.HasRiverOnEdge - accepts either side's bit, so both tiles are
		// marked here too.
		private static void MarkRiverStep(Tile tile, TileDirection dir) {
			if (!tile.neighbors.TryGetValue(dir, out Tile neighbor) || neighbor == Tile.NONE) {
				return;
			}

			foreach (Tile t in new[] { tile, neighbor }) {
				switch (dir) {
					case TileDirection.NORTHEAST:
					case TileDirection.SOUTHWEST:
						t.riverNortheast = true;
						t.riverSouthwest = true;
						break;
					case TileDirection.SOUTHEAST:
					case TileDirection.NORTHWEST:
						t.riverSoutheast = true;
						t.riverNorthwest = true;
						break;
				}
			}
		}


		// In Civ3, any desert tile with a river running along it becomes a
		// flood plain. Only tiles whose terrain is fully desert convert - hills
		// or vegetation on the tile make it a different terrain.
		internal static void AddFloodPlains(WorldCharacteristics wc, GameMap m) {
			TerrainType floodPlain = wc.terrainTypes.Find(x => x.Key == "flood plain");
			if (floodPlain == null) {
				log.Warning("Rules have no 'flood plain' terrain; skipping flood plain generation.");
				return;
			}

			foreach (Tile t in m.tiles) {
				if (!t.IsLand() || t.baseTerrainType.Key != "desert" || t.overlayTerrainType.Key != "desert") {
					continue;
				}

				if (t.BordersRiver()) {
					t.baseTerrainType = floodPlain;
					t.overlayTerrainType = floodPlain;
				}
			}
		}

		private static void FixContinentalShelf(WorldCharacteristics wc, GameMap m) {
			TerrainType coast = wc.terrainTypes.Find(x => x.Key == "coast");
			TerrainType sea = wc.terrainTypes.Find(x => x.Key == "sea");

			// Ensure that land only borders coast.
			foreach (Tile t in m.tiles) {
				if (t.baseTerrainType.Key != "ocean" && t.baseTerrainType.Key != "sea") {
					continue;
				}

				foreach (Tile neighbor in t.neighbors.Values) {
					if (neighbor != Tile.NONE && neighbor.IsLand()) {
						t.baseTerrainType = coast;
						t.overlayTerrainType = coast;
						break;
					}
				}
			}

			// Ensure that coast only borders sea, not ocean.
			foreach (Tile t in m.tiles) {
				if (t.baseTerrainType.Key != "ocean") {
					continue;
				}

				foreach (Tile neighbor in t.neighbors.Values) {
					if (neighbor.baseTerrainType.Key == "coast") {
						t.baseTerrainType = sea;
						t.overlayTerrainType = sea;
						break;
					}
				}
			}
		}

		// The resource pass at `0x5f22a0`. The rules, the addresses they were
		// read from and what the fork used to get wrong are written up in
		// re/notes/civ3_map_generator_spec.md section 2.11.
		//
		// Structure of the original, which this mirrors:
		//   * the tile index array is shuffled once, before the first pass, and
		//     the same array is reused by all three passes; the resource
		//     records are walked in record order and are NOT shuffled
		//     (`0x5f23ae`, `0x5f2860`, `0x5f2a7a`);
		//   * three class passes: luxuries first, then strategic resources,
		//     then everything that is neither - the bonus resources;
		//   * every candidate goes through the world vtable slot at `+0x44`,
		//     `Map_can_spawn_resource_at` @ `0x5f3320`.
		//
		// The stream this pass draws from is its own, seeded from the world seed.
		// That is an approximation, not the original's behaviour: the original
		// takes every one of these numbers from the generator's single
		// recurrence (`next_float` @ `0x60ba80`, wrapped by `rand_int` @
		// `0x60bab0`), whose state the whole generator advances and which the
		// passes that run earlier have already drawn from many times. Matching it
		// would mean reproducing every earlier pass' draw count, which is a port
		// of the whole generator's draw history rather than of this pass. It is
		// recorded as an approximation in re/notes/openciv3_generator_gaps.md
		// G5 and in section 2.11 of the generator spec.
		internal static void AddResources(WorldCharacteristics wc, GameMap m) {
			Random rand = new(wc.mapSeed + RESOURCE_SEED_OFFSET);

			List<int> tileIndicies = Enumerable.Range(0, m.tiles.Count).ToList();
			rand.Shuffle<int>(CollectionsMarshal.AsSpan(tileIndicies));

			// Luxury resources: pass 1, whose class test at `0x5e3720` is true
			// for a record whose `+0x3c` is one.
			//
			// Keep track of which continent a luxury resource is first placed
			// to ensure they don't get spread over multiple continents.
			Dictionary<Resource, int> resourceToContinentPlacement = new();
			foreach (Resource r in wc.resources) {
				if (r.Category != ResourceCategory.LUXURY) {
					continue;
				}

				PlaceLuxuryResourceType(rand, wc, m, r, tileIndicies, resourceToContinentPlacement);
			}

			// Strategic resources: pass 2, whose class test at `0x5e3730` is
			// true for `+0x3c` of two.
			foreach (Resource r in wc.resources) {
				if (r.Category != ResourceCategory.STRATEGIC) {
					continue;
				}

				PlaceStrategicResourceType(rand, wc, m, r, tileIndicies);
			}

			// Bonus resources: pass 3. The original keeps a resource whose
			// record is neither of the two classes above (the either-class test
			// at `0x5e3700` is false for both, `0x5f2a93`), which in the shipped
			// rules is the bonus category.
			List<Resource> bonusResources = wc.resources
				.Where(x => x.Category != ResourceCategory.LUXURY && x.Category != ResourceCategory.STRATEGIC)
				.ToList();
			PlaceBonusResources(rand, wc, m, bonusResources, tileIndicies);
		}

		// How much room a resource has, as the original counts it: one for each
		// terrain before index eleven that may carry it, five for each terrain
		// from index eleven on (`0x5f2447`-`0x5f249f`; the same loop appears in
		// all three passes). The field it reads is the resource-vs-terrain mask
		// of the terrain records, which in the shipped rules makes the terrains
		// past index ten exactly the water ones.
		internal static int TerrainWeight(WorldCharacteristics wc, Resource r) {
			int weight = 0;
			for (int i = 0; i < wc.terrainTypes.Count; ++i) {
				if (wc.terrainTypes[i].allowedResources.Contains(r.Key)) {
					weight += (i > 10) ? 5 : 1;
				}
			}
			return weight;
		}

		// The count a resource is placed up to. The original draws
		// 50 + rand_int(26) + rand_int(26) when the record states no appearance
		// ratio (`0x5f23e3`-`0x5f2407`), scales by the number of civs over one
		// hundred (`0x5f2411`-`0x5f242a`), then scales again by the eligible
		// terrain weight - halved for a weight of one, three quarters for a
		// weight of two or three, untouched for four or more - and floors the
		// result at one, or at two when the weight is four or more
		// (`0x5f24ae`-`0x5f24f1`). The bonus pass uses only the weight, not the
		// count.
		internal static int GetAppearance(WorldCharacteristics wc, Random rand, Resource r, int terrainWeight) {
			int baseCount = r.AppearanceRatio;
			if (baseCount == 0) {
				baseCount = DrawDefaultAppearanceRatio(rand);
			}

			int targetCount = (wc.worldSize.numberOfCivs * baseCount) / 100;
			if (terrainWeight < 2) {
				targetCount = targetCount / 2;
			} else if (terrainWeight < 4) {
				targetCount = (targetCount * 3) / 4;
			}

			int minCount = (terrainWeight >= 4) ? 2 : 1;
			return Math.Max(minCount, targetCount);
		}

		// 50 plus two uniform draws over 0..25, so a triangular 50..100
		// (`0x5f23e3`-`0x5f2407`). The fork used to add five draws over 0..10,
		// which is the same range with a different and higher-biased shape.
		internal static int DrawDefaultAppearanceRatio(Random rand) {
			return 50 + rand.Next(26) + rand.Next(26);
		}

		// The rank distance the candidacy predicate's spacing loop covers. The
		// original's loop bound is nine - the eight neighbours - for anything
		// that is neither a luxury nor a strategic resource, and the square of
		// `min(2 * ((width + height) / 100) + 5, 13)` otherwise (`0x5f3450`-
		// `0x5f34e0` and `0x5f34ca`). A bound of n^2 is exactly the first
		// (n - 1) / 2 rings of the spiral, so the rank is (n - 1) / 2:
		// (width + height) / 100 + 2, capped at six.
		internal static int ResourceSpacingRank(GameMap m, Resource r) {
			if (r.Category != ResourceCategory.LUXURY && r.Category != ResourceCategory.STRATEGIC) {
				return 1;
			}
			return Math.Min((m.numTilesWide + m.numTilesTall) / 100 + 2, 6);
		}

		// The die the bonus pass rolls per resource per round: six sides for a
		// terrain weight of one, four for two or three, two for four or more,
		// with a placement attempted when the roll is zero or one, so the
		// probability is two over the number of sides (`0x5f2b32`-`0x5f2b66`).
		internal static int BonusDieSides(int terrainWeight) {
			if (terrainWeight < 2) {
				return 6;
			} else if (terrainWeight < 4) {
				return 4;
			}
			return 2;
		}

		// The candidacy predicate, `Map_can_spawn_resource_at` @ `0x5f3320`,
		// reached through the world's vtable slot at +0x44. `doubleMinBodySize`
		// is the caller's fourth argument: the luxury pass asks for the larger
		// body over the first two thirds of its tile walk, and the other two
		// passes always ask for it (`0x5f337b`-`0x5f3398`).
		//
		// The predicate also refuses a tile that carries the potential shield
		// bonus (`0x5f3445`-`0x5f344a`): it asks the tile's slot at +0x6c and
		// rejects the tile when the answer is set. That answer is one bit - the
		// kind-2 flag word at tile +0x30, shifted down sixteen and cut to its
		// lowest bit (`0x5ea930`) - and not the whole high byte of the word it
		// was read from. Bit 29 of that word is the landmark flag (`0x5ea980`,
		// set by `0x5ea990`) and the predicate does not mind it.
		//
		// A generated map never reaches this refusal already set: the pass that
		// sets the bit is the dense-feature pass (`0x5f2090`, the bonus-grassland
		// pass here), and both the driver (`0x5eb580`, which runs it at
		// `0x5eb783`, after the resource pass at `0x5eb763`) and this fork's
		// GenerateMap put it after resources. The refusal is not inert in the
		// original, though, because the same predicate is asked again by the
		// resource-upkeep path (`0x4f4cb0` at `0x4f4e6a`, then `0x5d5d00` at
		// `0x5d6262`) over a map whose tiles kept the mark from generation. That
		// path has no counterpart here, so what this fork carries is the
		// predicate's rule at the predicate's own consultation.
		//
		// The terrain and resource record lookups the original performs first
		// only cache, so they have no counterpart here.
		internal static bool CanPlaceResource(WorldCharacteristics wc, GameMap m, Resource r, Tile t, bool doubleMinBodySize) {
			// A tile that already carries a resource is never a candidate
			// (`0x5f341c`).
			if (HasResource(t)) {
				return false;
			}

			// Nor a tile marked with the potential shield bonus (`0x5f3445`).
			if (t.isBonusShield) {
				return false;
			}

			// The resource must be legal on the tile's terrain (`0x5f33da`-
			// `0x5f3416`).
			if (!t.overlayTerrainType.allowedResources.Contains(r.Key)) {
				return false;
			}

			// A luxury needs a large enough body: 37 tiles, or 75 when the
			// caller doubled the minimum (`0x5f3367`-`0x5f3398`). The body is
			// the original's "continent id", which covers water as well as
			// land.
			if (r.Category == ResourceCategory.LUXURY) {
				int minBodySize = doubleMinBodySize ? 75 : 37;
				if (BodySize(m, t) < minBodySize) {
					return false;
				}
			}

			// Spacing (`0x5f34ee`-`0x5f3610`). Only tiles on the candidate's own
			// body count.
			int spacingRank = ResourceSpacingRank(m, r);
			foreach (Tile x in t.GetTilesWithinRankDistance(spacingRank)) {
				if (x == t || x.continent != t.continent || !HasResource(x)) {
					continue;
				}

				bool sameResource = x.Resource.Key == r.Key;
				if (t.RankDistanceTo(x) <= 1) {
					// Among the eight neighbours any other resource blocks
					// every class, and holding this very resource blocks too
					// only for a strategic one (`0x5f35bf`-`0x5f35d3`).
					if (!sameResource || r.Category == ResourceCategory.STRATEGIC) {
						return false;
					}
				} else if (r.Category == ResourceCategory.STRATEGIC) {
					// Further out a strategic resource minds only its own kind
					// (`0x5f35d5`-`0x5f35ea`).
					if (sameResource) {
						return false;
					}
				} else if (r.Category == ResourceCategory.LUXURY) {
					// ... and a luxury only a different luxury
					// (`0x5f35ec`-`0x5f360e`).
					if (!sameResource && x.Resource.Category == ResourceCategory.LUXURY) {
						return false;
					}
				}
			}

			// A water resource needs land inside its big fat cross - the first
			// twenty positions of the spiral (`0x5f363e`-`0x5f370d`).
			if (!t.IsLand() && !HasSufficientLandNeighborsForResource(wc, t)) {
				return false;
			}

			return true;
		}

		// The area of the body a tile sits on, which the original reads through
		// the world's body accessor at +0x84 (record field +0x24).
		private static int BodySize(GameMap m, Tile t) {
			HashSet<Tile> continent = m.continents.First(x => x.Contains(t));
			return continent.Count;
		}

		// A water resource has to be near land: the last step of the candidacy
		// predicate asks that at least one tile of the candidate's big fat cross
		// be land (`0x5f365e`-`0x5f370d`). The rank the fork passes is the same
		// rules value the original's cross is built from.
		private static bool HasSufficientLandNeighborsForResource(WorldCharacteristics wc, Tile t) {
			int landTiles = 0;

			foreach (Tile x in t.GetTilesWithinRankDistance(wc.maxRankOfWorkableTiles)) {
				if (x.IsLand()) {
					++landTiles;
				}
			}

			return landTiles >= 1;
		}

		// The clumping guard of the luxury pass' spreading step
		// (`0x5f26dd`-`0x5f278c`): how many of the candidate's eight neighbours
		// already hold this resource, refused at three or more.
		internal static bool ClumpGuardRefuses(Tile t, Resource r) {
			int sameResourceNeighbours = 0;
			foreach (Tile n in t.neighbors.Values) {
				if (n != null && n != Tile.NONE && HasResource(n) && n.Resource.Key == r.Key) {
					++sameResourceNeighbours;
				}
			}
			return sameResourceNeighbours >= 3;
		}

		// The flag the luxury pass hands the candidacy predicate: the first two
		// thirds of the shuffled tile walk ask for the bigger minimum body size
		// (`0x5f256e`-`0x5f258b`).
		private static bool DoubleMinBodySize(int tileOrderIndex, int tileCount) {
			return (tileOrderIndex + 1) < (2 * tileCount / 3);
		}

		// The tile the luxury pass clumps onto: the first of the origin's eight
		// neighbours, in the original's spiral order, that both the clumping
		// guard and the candidacy predicate accept (`0x5f263a`-`0x5f27c3`).
		internal static Tile FindClumpNeighbour(WorldCharacteristics wc, GameMap m, Resource r, Tile origin, bool doubleMinBodySize) {
			for (int index = 1; index <= 8; ++index) {
				(int dx, int dy) = Civ3SpiralOffset(index);
				Tile n = m.tileAt(origin.XCoordinate + dx, origin.YCoordinate + dy);
				if (n == null || n == Tile.NONE || n == origin) {
					continue;
				}

				if (ClumpGuardRefuses(n, r)) {
					continue;
				}

				if (CanPlaceResource(wc, m, r, n, doubleMinBodySize)) {
					return n;
				}
			}

			return null;
		}

		private static void PlaceResource(Tile t, Resource r) {
			t.Resource = r;
			t.ResourceKey = r.Key;
		}

		private static void PlaceLuxuryResourceType(Random rand, WorldCharacteristics wc, GameMap m, Resource r, List<int> tileIndicies, Dictionary<Resource, int> resourceToContinentPlacement) {
			int targetCount = GetAppearance(wc, rand, r, TerrainWeight(wc, r));
			int placed = 0;
			resourceToContinentPlacement[r] = -1;

			// The original walks the shuffled tiles from the start, and after
			// every placement it walks them from the start again (`0x5f2505`
			// is re-entered with its index reset). A walk that placed nothing
			// cannot place anything on a re-walk of an unchanged map, so the
			// walk is where the resource gives up.
			int i = 0;
			while (placed < targetCount && i < tileIndicies.Count) {
				Tile t = m.tiles[tileIndicies[i]];
				bool doubleMinBodySize = DoubleMinBodySize(i, tileIndicies.Count);

				// One body per luxury: once the first copy is down, only tiles
				// on that body are considered (`0x5f2536`-`0x5f255d`).
				if (resourceToContinentPlacement[r] != -1 && resourceToContinentPlacement[r] != t.continent) {
					++i;
					continue;
				}

				if (!CanPlaceResource(wc, m, r, t, doubleMinBodySize)) {
					++i;
					continue;
				}

				// Place the resource.
				PlaceResource(t, r);
				resourceToContinentPlacement[r] = t.continent;

				// Then give ourselves the chance to place additional instances
				// of this luxury in a clump. The original draws a coin before
				// every pass over the eight neighbours and stops on its second
				// face; a pass that places nothing is retried, which is what
				// the original does too, so a retry only ever costs the coin
				// draws. The eight neighbours are always those of the tile just
				// placed, not of whatever the clump placed (`0x5f2612`-`0x5f2626`).
				while (true) {
					if (rand.Next(2) != 0) {
						break;
					}

					Tile neighbour = FindClumpNeighbour(wc, m, r, t, doubleMinBodySize);
					if (neighbour != null) {
						PlaceResource(neighbour, r);
						++placed;
					}

					if (placed >= targetCount) {
						break;
					}
				}

				// The original counts the tile it placed on after the clump
				// loop, not before, so a fully successful clump can overshoot
				// the target by one (`0x5f2809`-`0x5f2828`).
				++placed;
				i = 0;
			}

			if (placed < targetCount) {
				log.Information($"Only placed {placed} of {targetCount} {r.Key}");
			}
		}

		private static void PlaceStrategicResourceType(Random rand, WorldCharacteristics wc, GameMap m, Resource r, List<int> tileIndicies) {
			int targetCount = GetAppearance(wc, rand, r, TerrainWeight(wc, r));
			int placed = 0;

			// The original makes `target` attempts, and each attempt walks the
			// shuffled tiles from the start and places on the first candidate
			// it finds (`0x5f29a9`-`0x5f2a3e`). It hands the predicate the
			// larger minimum body size, which the gate only applies to
			// luxuries, so it has no effect on a strategic resource.
			for (int attempt = 0; attempt < targetCount; ++attempt) {
				for (int i = 0; i < tileIndicies.Count; ++i) {
					Tile t = m.tiles[tileIndicies[i]];
					if (!CanPlaceResource(wc, m, r, t, true)) {
						continue;
					}

					PlaceResource(t, r);
					++placed;
					break;
				}
			}

			if (placed < targetCount) {
				log.Information($"Only placed {placed} of {targetCount} {r.Key}");
			}
		}

		private static void PlaceBonusResources(Random rand, WorldCharacteristics wc, GameMap m,
												List<Resource> bonusResources, List<int> tileIndicies) {
			int totalPossibleBonusResources = m.tiles.Count / 32;
			int placed = 0;

			// Each round offers every bonus resource one placement attempt, in
			// record order, and a round that places nothing ends the pass
			// (`0x5f2a7a`-`0x5f2c2e`).
			while (true) {
				int placedBeforeThisRound = placed;

				foreach (Resource r in bonusResources) {
					int terrainWeight = TerrainWeight(wc, r);
					if (terrainWeight == 0) {
						continue;
					}

					if (rand.Next(BonusDieSides(terrainWeight)) >= 2) {
						continue;
					}

					foreach (int index in tileIndicies) {
						Tile t = m.tiles[index];
						if (!CanPlaceResource(wc, m, r, t, true)) {
							continue;
						}

						PlaceResource(t, r);
						++placed;
						break;
					}
				}

				if (placed == placedBeforeThisRound || placed >= totalPossibleBonusResources) {
					break;
				}
			}
		}

		private static void AddBarbarianCamps(WorldCharacteristics wc, GameMap m) {
			Random rand = new(wc.mapSeed + 0xba7b5);
			List<int> tileIndicies = Enumerable.Range(0, m.tiles.Count).ToList();
			rand.Shuffle<int>(CollectionsMarshal.AsSpan(tileIndicies));

			int landTiles = 0;
			foreach (Tile t in m.tiles) {
				if (t.IsLand()) {
					++landTiles;
				}
			}

			int totalPossibleBarbCamps = DeriveTotalPossibleBarbCamps(wc, landTiles);

			// The tribes the camps already placed here hold. Civ3 never places
			// camps at map generation, so its rule - the band of the culture group
			// of the nearest city's owner - has no input on a generated map: there
			// is no city yet. The band is therefore drawn uniformly, which keeps
			// every shipped tribe name reachable and, more importantly, gives every
			// generated camp a real slot so its dispersal can free it and its
			// messages can name it.
			List<int> heldSlots = new();

			int numCamps = 0;
			for (int i = 0; i < tileIndicies.Count && numCamps < totalPossibleBarbCamps; ++i) {
				Tile t = m.tiles[tileIndicies[i]];
				if (IsValidForBarbarianCamp(wc, m, t)) {
					int cultureGroup = rand.Next(BarbarianTribes.CultureGroupCount);
					t.barbarianTribeId = BarbarianTribes.PickTribeSlot(
						cultureGroup, heldSlots, rand.Next(BarbarianTribes.SlotsPerCultureGroup));
					heldSlots.Add(t.barbarianTribeId);
					m.barbarianCamps.Add(t);
					t.hasBarbarianCamp = true;
					++numCamps;
				}
			}
		}

		/// <summary>
		/// Apply barbarian activity level to barbarian camp spawn rate. Currently NOT based on Civ3 values.
		/// TODO: Make configurable
		/// TODO: Determine what these values are in Civ3 
		/// </summary>
		private static int DeriveTotalPossibleBarbCamps(WorldCharacteristics wc, int landTiles) {
			var totalCampsBaseline = landTiles / 100;
			switch (wc.barbarianActivity) {
				case BarbarianActivity.None:
					return 0;
				case BarbarianActivity.Sedentary:
					return totalCampsBaseline;
				case BarbarianActivity.Roaming:
					return totalCampsBaseline;
				case BarbarianActivity.Restless:
					return (int)Math.Round(totalCampsBaseline * 1.25); // extra 25%
				case BarbarianActivity.Raging:
					return (int)Math.Round(totalCampsBaseline * 1.50); // extra 50%
				default:
					log.Warning("Unknown Barbarian Activity at barb camps derivation.");
					return totalCampsBaseline;
			}
		}

		private static bool IsValidForBarbarianCamp(WorldCharacteristics wc, GameMap m, Tile t) {
			// No barbarian camps on water, volcanos, or mountains.
			if (!t.IsLand() || t == Tile.NONE || t.overlayTerrainType.Key == "volcano" || t.overlayTerrainType.Key == "mountains") {
				return false;
			}

			// No barbarian camps on luxury or strategic resources (we're ok
			// with barb camps on things like cows or gold).
			if (t.Resource != null && t.Resource != Resource.NONE &&
				(t.Resource.Category == ResourceCategory.STRATEGIC || t.Resource.Category == ResourceCategory.LUXURY)) {
				return false;
			}

			// No barbarian camps on super tiny islands.
			HashSet<Tile> continent = m.continents.First(x => x.Contains(t));
			if (continent.Count < 15) {
				return false;
			}

			// No barbarian camps within the big fat cross of another camp.
			foreach (Tile n in t.GetTilesWithinRankDistance(wc.maxRankOfBarbarianCampTiles)) {
				if (n.hasBarbarianCamp) {
					return false;
				}
			}

			// No barbarian camps less than 6 tiles away from a player
			if (m.startingLocations.Any(x => t.DistanceTo(x) < 6)) return false;

			return true;
		}

		private static void AddGoodyHuts(WorldCharacteristics wc, GameMap m) {
			// With "No Barbarians" the generated map has no goody huts at all.
			if (wc.barbarianActivity == BarbarianActivity.None) {
				return;
			}

			// The pass is skipped entirely on degenerate maps.
			int tileCount = m.tiles.Count;
			if ((tileCount & 0xfff0) == 0) {
				return;
			}

			// One independent draw per attempt, roughly one attempt per 32
			// tiles. There is no de-duplication and no second-chance pass, so
			// the realised hut count is well below the attempt count because
			// the predicate enforces spacing.
			Random rand = new(wc.mapSeed + 0x900d);
			int attempts = tileCount >> 5;
			for (int i = 0; i < attempts; ++i) {
				Tile t = m.tiles[rand.Next(tileCount)];
				if (IsValidForGoodyHut(m, t)) {
					t.hasGoodyHut = true;
				}
			}
		}

		/// <summary>
		/// A goody hut may only be placed on land that carries no starting
		/// location, barbarian camp, city, unit or resource, and no other hut
		/// within the 7x7 Chebyshev block around it - so huts are at least four
		/// tiles apart.
		/// </summary>
		private static bool IsValidForGoodyHut(GameMap m, Tile t) {
			if (t == Tile.NONE || !t.IsLand()) {
				return false;
			}
			if (m.startingLocations.Contains(t) || t.hasBarbarianCamp || t.hasGoodyHut) {
				return false;
			}
			if (t.HasCity() || t.unitsOnTile.Count > 0) {
				return false;
			}
			if (t.Resource != null && t.Resource != Resource.NONE) {
				return false;
			}

			foreach (Tile n in t.GetTilesWithinTileSquare(3)) {
				if (n != null && n != Tile.NONE && n.hasGoodyHut) {
					return false;
				}
			}

			return true;
		}

		public static void AddBonusGrasslands(WorldCharacteristics wc, GameMap m) {
			// Randomize the order we iterate through the tiles.
			Random rand = new(wc.mapSeed + 0x7361);
			List<int> tileIndicies = Enumerable.Range(0, m.tiles.Count).ToList();
			rand.Shuffle<int>(CollectionsMarshal.AsSpan(tileIndicies));

			foreach (int index in tileIndicies) {
				Tile t = m.tiles[index];

				// We only want grassland tiles without hills. A forest/jungle/marsh
				// is ok, since that can be cleared.
				if (t.baseTerrainType.Key != "grassland") {
					continue;
				}

				// Each grassland tile has a 25% chance.
				if (rand.Next(100) >= 25) {
					continue;
				}

				// We can't have bonus grassland under a resource/luxury.
				bool hasResource = !(t.Resource == Resource.NONE || t.Resource == null);
				if (hasResource) {
					continue;
				}

				// We can't have bonus grassland under a hill/mountain.
				if (t.overlayTerrainType.isHilly()) {
					continue;
				}

				t.isBonusShield = true;
			}
		}
	}
}
