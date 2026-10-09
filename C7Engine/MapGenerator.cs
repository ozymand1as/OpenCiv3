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
		public Government defaultGovernment = new();

		public int maxRankOfWorkableTiles;
		public int maxRankOfBarbarianCampTiles;

		public WorldCharacteristics() { }

		public WorldCharacteristics(SaveGame save) {
			terrainTypes = save.TerrainTypes;
			resources = save.Resources;
			defaultGovernment = save.Governments.Find(g => g.defaultType);

			maxRankOfWorkableTiles = save.Rules.MaxRankOfWorkableTiles;
			maxRankOfBarbarianCampTiles = save.Rules.MaxRankOfBarbarianCampTiles;

			barbarianActivity = save.BarbarianInfo.barbarianActivity;
		}
	}

	public class MapGenerator {
		private static ILogger log = Log.ForContext<MapGenerator>();

		private const int MIN_TILES_PER_PLAYER_ISLAND = 81;

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

		private static void AddResources(WorldCharacteristics wc, GameMap m) {
			Random rand = new(wc.mapSeed + 0x7171);

			// Randomize the order resources are placed and the order we go
			// through tiles.
			List<Resource> resourcesToPlace = new();
			foreach (Resource r in wc.resources) {
				log.Information(r.Key);
				resourcesToPlace.Add(r);
			}
			rand.Shuffle<Resource>(CollectionsMarshal.AsSpan(resourcesToPlace));

			List<int> tileIndicies = Enumerable.Range(0, m.tiles.Count).ToList();
			rand.Shuffle<int>(CollectionsMarshal.AsSpan(tileIndicies));

			// Luxury resources.
			//
			// Keep track of which continent a luxury resource is first placed
			// to ensure they don't get spread over multiple continents.
			Dictionary<Resource, int> resourceToContinentPlacement = new();
			foreach (Resource r in resourcesToPlace) {
				if (r.Category != ResourceCategory.LUXURY) {
					continue;
				}

				PlaceLuxuryResourceType(rand, wc, m, r, tileIndicies, resourceToContinentPlacement);
			}

			// Strategic resources.
			foreach (Resource r in resourcesToPlace) {
				if (r.Category != ResourceCategory.STRATEGIC) {
					continue;
				}

				PlaceStrategicResourceType(rand, wc, m, r, tileIndicies);
			}

			// Bonus resources.
			PlaceBonusResources(rand, wc, m, resourcesToPlace.Where(x => x.Category == ResourceCategory.BONUS).ToList(), tileIndicies);
		}

		// Determine the rate of appearance for a given resource. The rate of
		// appearance seems to have a default of 100 for civ3, but if it isn't
		// specified (like for luxuries) it is some random value less than 100
		// but always larger than 50.
		private static int GetAppearance(WorldCharacteristics wc, Random rand, Resource r, int minCount) {
			// The the appearance ratio, with a random number between 50 and
			// 100 if it isn't specified. Use multiple random calls to roughly
			// simulate a normal distribution using uniform random samples.
			int baseCount = r.AppearanceRatio;
			if (baseCount == 0) {
				baseCount = 50 + rand.Next(11) + rand.Next(11) + rand.Next(11) + rand.Next(11) + rand.Next(11);
			}

			// Scale the appearance based on the number of civs in the game.
			int targetCount = (wc.worldSize.numberOfCivs * baseCount) / 100;
			targetCount = Math.Max(minCount, targetCount);
			return targetCount;
		}

		private static void PlaceLuxuryResourceType(Random rand, WorldCharacteristics wc, GameMap m, Resource r, List<int> tileIndicies, Dictionary<Resource, int> resourceToContinentPlacement) {
			int targetCount = GetAppearance(wc, rand, r, minCount:1);
			int placed = 0;
			resourceToContinentPlacement[r] = -1;

			for (int i = 0; placed < targetCount && i < tileIndicies.Count; ++i) {
				Tile t = m.tiles[tileIndicies[i]];

				// Skip tiles where we can't place this resource.
				if (!t.overlayTerrainType.allowedResources.Contains(r.Key)) {
					continue;
				}

				// Skip tiles that are already next to a resource.
				if (IsNextToExistingResource(t)) {
					continue;
				}

				// If we have a continent for this luxury and this tile isn't
				// on that continent, skip it.
				if (resourceToContinentPlacement[r] != -1 && resourceToContinentPlacement[r] != t.continent) {
					continue;
				}

				// Skip tiles that don't need the luxury-specific criteria.
				if (!IsValidForLuxuryPlacement(wc, m, r, t)) {
					continue;
				}

				// Place the resource.
				++placed;
				t.Resource = r;
				t.ResourceKey = r.Key;
				resourceToContinentPlacement[r] = t.continent;

				// Give ourselves the chance to place additional instances of
				// this luxury in a clump.
				for (int clusterAttempt = 0; clusterAttempt < 4 && placed < targetCount && rand.Next(100) < 50; ++clusterAttempt) {
					Tile neighbor = t.neighbors.Values
						.Where(x => x != Tile.NONE
									&& x.overlayTerrainType.allowedResources.Contains(r.Key)
									&& x.continent == t.continent)
						.OrderBy(x => rand.Next()) // Shuffle the neighbors
						.FirstOrDefault(Tile.NONE);

					if (neighbor == Tile.NONE) {
						break;
					}
					++placed;
					neighbor.Resource = r;
					neighbor.ResourceKey = r.Key;
				}
			}

			if (placed < targetCount) {
				log.Information($"Only placed {placed} of {targetCount} {r.Key}");
			}
		}

		private static bool IsNextToExistingResource(Tile t) {
			foreach (Tile neighbor in t.neighbors.Values) {
				if (neighbor.Resource != null && neighbor.Resource != Resource.NONE) {
					return true;
				}
			}
			return false;
		}

		private static bool IsValidForLuxuryPlacement(WorldCharacteristics wc, GameMap m, Resource r, Tile t) {
			HashSet<Tile> continent = m.continents.First(x => x.Contains(t));

			// Don't put luxuries on islands too small for players.
			if (continent.Count < MIN_TILES_PER_PLAYER_ISLAND) {
				return false;
			}

			int minLuxurySpacing = (m.numTilesTall + m.numTilesWide) / 40;
			minLuxurySpacing = Math.Max(2, minLuxurySpacing);
			minLuxurySpacing = Math.Min(minLuxurySpacing, 10);

			foreach (Tile x in t.GetTilesWithinRankDistance(minLuxurySpacing)) {
				if (x.Resource != null
					&& x.Resource != Resource.NONE
					&& x.Resource.Category == ResourceCategory.LUXURY
					&& x.Resource.Key != r.Key) {
					return false;
				}
			}

			// If this is a water-based resource, ensure it doesn't end up in the
			// middle of the ocean.
			if (!t.IsLand() && !HasSufficientLandNeighborsForResource(wc, t)) {
				return false;
			}

			return true;
		}

		private static bool HasSufficientLandNeighborsForResource(WorldCharacteristics wc, Tile t) {
			int landTiles = 0;

			foreach (Tile x in t.GetTilesWithinRankDistance(wc.maxRankOfWorkableTiles)) {
				if (x.IsLand()) {
					++landTiles;
				}
			}

			return landTiles >= 1;
		}

		private static void PlaceStrategicResourceType(Random rand, WorldCharacteristics wc, GameMap m, Resource r, List<int> tileIndicies) {
			int targetCount = GetAppearance(wc, rand, r, minCount:2);
			int placed = 0;

			for (int i = 0; placed < targetCount && i < tileIndicies.Count; ++i) {
				Tile t = m.tiles[tileIndicies[i]];

				// Skip tiles where we can't place this resource.
				if (!t.overlayTerrainType.allowedResources.Contains(r.Key)) {
					continue;
				}

				// Skip tiles that are already next to a resource.
				if (IsNextToExistingResource(t)) {
					continue;
				}

				// Skip tiles that don't need the strategic resource-specific criteria.
				if (!IsValidForStrategicResourcePlacement(wc, m, r, t)) {
					continue;
				}

				// Place the resource.
				++placed;
				t.Resource = r;
				t.ResourceKey = r.Key;
			}

			if (placed < targetCount) {
				log.Information($"Only placed {placed} of {targetCount} {r.Key}");
			}
		}

		private static bool IsValidForStrategicResourcePlacement(WorldCharacteristics wc, GameMap m, Resource r, Tile t) {
			HashSet<Tile> continent = m.continents.First(x => x.Contains(t));

			// Don't put strategic resources on super tiny islands - though
			// putting them on small islands is ok.
			if (continent.Count < MIN_TILES_PER_PLAYER_ISLAND / 2) {
				return false;
			}

			int minSpacing = (m.numTilesTall + m.numTilesWide) / 30;
			minSpacing = Math.Max(2, minSpacing);
			minSpacing = Math.Min(minSpacing, 10);

			// Ensure strategic resources of the same kind don't clump up.
			foreach (Tile x in t.GetTilesWithinRankDistance(minSpacing)) {
				if (x.Resource != null
					&& x.Resource != Resource.NONE
					&& x.Resource.Category == ResourceCategory.STRATEGIC
					&& x.Resource.Key == r.Key) {
					return false;
				}
			}

			// If this is a water-based resource, ensure it doesn't end up in the
			// middle of the ocean.
			if (!t.IsLand() && !HasSufficientLandNeighborsForResource(wc, t)) {
				return false;
			}

			return true;
		}

		private static void PlaceBonusResources(Random rand, WorldCharacteristics wc, GameMap m,
												List<Resource> bonusResources, List<int> tileIndicies) {
			int totalPossibleBonusResources = m.tiles.Count / 32;
			int placed = 0;

			Dictionary<Resource, int> terrainScores = CalculateBonusResourceTerrainScores(wc, bonusResources);

			for (int pass = 0; pass < 32 && placed < totalPossibleBonusResources; ++pass) {
				rand.Shuffle<Resource>(CollectionsMarshal.AsSpan(bonusResources));

				foreach (Resource r in bonusResources) {
					int terrainScore = terrainScores[r];

					// Can't be placed anywhere.
					if (terrainScore == 0) {
						continue;
					}

					// Resources that can go in more places have a higher chance
					// of being placed.
					int placementProbability = 25;
					if (terrainScore < 2) {
						placementProbability = 16;
					} else if (terrainScore > 3) {
						placementProbability = 50;
					}

					if (rand.Next(100) >= placementProbability) {
						continue;
					}

					if (PlaceBonusResource(wc, m, r, tileIndicies)) {
						++placed;
					}
				}
			}
		}

		private static bool PlaceBonusResource(WorldCharacteristics wc, GameMap m, Resource r, List<int> tileIndicies) {
			// We want to place this resource. Find the first valid tile
			// we can stick it on.
			//
			// We don't want it next to other resources, and if it is a
			// water resource (fish/whale/etc) don't stick it in the
			// middle of the ocean.
			foreach (int index in tileIndicies) {
				Tile t = m.tiles[index];
				if (!t.overlayTerrainType.allowedResources.Contains(r.Key)) {
					continue;
				}

				bool hasResource = !(t.Resource == Resource.NONE || t.Resource == null);
				if (hasResource || IsNextToExistingResource(t)) {
					continue;
				}

				if (!t.IsLand() && !HasSufficientLandNeighborsForResource(wc, t)) {
					continue;
				}

				t.Resource = r;
				t.ResourceKey = r.Key;
				return true;
			}
			return false;
		}

		private static Dictionary<Resource, int> CalculateBonusResourceTerrainScores(WorldCharacteristics wc, List<Resource> bonusResources) {
			Dictionary<Resource, int> result = new();

			foreach (Resource r in bonusResources) {
				int score = 0;
				foreach (TerrainType tt in wc.terrainTypes) {
					if (tt.allowedResources.Contains(r.Key)) {
						// Make water bonus resources get a higher score, since
						// land bonus resources are generally more valuable.
						if (tt.isWater()) {
							score += 4;
						} else {
							score += 1;
						}
					}
				}
				result[r] = score;
			}

			return result;
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

		private static void DetermineStartingLocations(WorldCharacteristics wc, GameMap m) {
			Random rand = new(wc.mapSeed + 0x1337);
			List<HashSet<Tile>> landContinents = m.continents.Where(x => x.First().IsLand()).ToList();

			// Count how many luxuries each continent has.
			Dictionary<int, int> continentLuxuryCount = new();
			foreach (HashSet<Tile> continent in landContinents) {
				continentLuxuryCount[continent.First().continent] = 0;
				foreach (Tile t in continent) {
					if (t.Resource.Category == ResourceCategory.LUXURY) {
						continentLuxuryCount[t.continent]++;
					}
				}
			}

			// Find all viable city locations.
			Dictionary<Tile, int> scoredTiles = new();
			foreach (Tile t in m.tiles) {
				int score = ScorePossibleCityLocation(wc, t);
				if (score > 0) {
					scoredTiles[t] = score;
				}
			}
			List<Tile> orderedTiles = scoredTiles.OrderByDescending(t => t.Value).Select(x => x.Key).ToList();

			// Use those to find the appropriate number of starting locations.
			List<Tile> startingLocations = new();
			Dictionary<int, int> continentStartingLocationCount = new();

			for (int attempt = 0; attempt < 10 && startingLocations.Count < wc.worldSize.numberOfCivs; ++attempt) {
				foreach (Tile t in orderedTiles) {
					if (startingLocations.Count == wc.worldSize.numberOfCivs) {
						break;
					}

					if (!IsContinentLargeEnough(m, t, attempt)) {
						continue;
					}

					// TODO: We need to consider the number of seafaring civs,
					// since they need to start on a coastal tile.

					if (!ContinentHasEnoughLuxuries(t, continentLuxuryCount, continentStartingLocationCount, attempt)) {
						continue;
					}

					if (TileIsTooCloseToOtherStarts(t, startingLocations, wc.worldSize.distanceBetweenCivs, attempt)) {
						continue;
					}

					// This is a valid starting location.
					startingLocations.Add(t);
					if (continentStartingLocationCount.ContainsKey(t.continent)) {
						continentStartingLocationCount[t.continent]++;
					} else {
						continentStartingLocationCount[t.continent] = 1;
					}
					log.Information($"Placed start number {startingLocations.Count} on attempt {attempt}");
				}
			}

			if (wc.worldSize.numberOfCivs > startingLocations.Count)
				log.Error("More civs than available starting locations.");

			// Before using the starting locations, shuffle them, so that the
			// human player doesn't always get the best starting spot.
			rand.Shuffle<Tile>(CollectionsMarshal.AsSpan(startingLocations));
			m.startingLocations = startingLocations;
		}

		// Allow smaller continents the more desperate we are to find a starting
		// location. Hopefully map generation will have handled this, but we
		// do bail out of map generation eventually.
		private static bool IsContinentLargeEnough(GameMap m, Tile t, int attempt) {
			HashSet<Tile> continent = m.continents.First(x => x.Contains(t));

			if (attempt < 3) {
				return continent.Count > MIN_TILES_PER_PLAYER_ISLAND;
			} else if (attempt < 6) {
				return continent.Count > MIN_TILES_PER_PLAYER_ISLAND / 2;
			}
			return true;
		}

		// Try to match the behavior of at least one luxury per player per continent.
		private static bool ContinentHasEnoughLuxuries(Tile t, Dictionary<int, int> continentLuxuryCount, Dictionary<int, int> continentStartingLocationCount, int attempt) {
			// Only be strict on the first two passes.
			if (attempt >= 2) {
				return true;
			}

			int luxuriesOnContinent = 0;
			if (continentLuxuryCount.ContainsKey(t.continent)) {
				luxuriesOnContinent = continentLuxuryCount[t.continent];
			}

			int startsOnContinent = 0;
			if (continentStartingLocationCount.ContainsKey(t.continent)) {
				startsOnContinent = continentStartingLocationCount[t.continent];
			}

			return startsOnContinent < luxuriesOnContinent;
		}

		private static bool TileIsTooCloseToOtherStarts(Tile t, List<Tile> startingLocations, int minDistance, int attempt) {
			if (attempt > 2) {
				minDistance /= 2;
			}

			if (attempt > 3) {
				minDistance -= attempt;
				minDistance = Math.Max(minDistance, 3); // hard floor
			}

			foreach (Tile start in startingLocations) {
				if (start.continent == t.continent && start.DistanceTo(t) < minDistance) {
					return true;
				}
			}

			return false;
		}

		// TODO: merge this with the ai logic
		private static int ScorePossibleCityLocation(WorldCharacteristics wc, Tile t) {
			if (!t.IsAllowCities()) {
				return int.MinValue;
			}

			const int CommercePoints = 4;
			const int ShieldPoints = 12;
			const int FoodPoints = 20;
			const int RiverPoints = 35;
			const int CoastPoints = 30;
			const int LandTilePoints = 1;
			const int BarbarianCampPoints = -50;

			Player player = new();
			player.government = wc.defaultGovernment;

			// Calculate the score for tiles in the immediate area.
			int score = 0;
			foreach (Tile n in t.GetTilesWithinRankDistance(1)) {
				score += CommercePoints * n.CommerceYield(player).yield;
				score += ShieldPoints * n.ProductionYield(player).yield;
				score += FoodPoints * n.FoodYield(player).yield;
				if (n.hasBarbarianCamp) {
					score += BarbarianCampPoints;
				}

				if (!n.IsLand()) {
					score += CoastPoints;
				}
			}

			// Then do it again for the full big fat cross, effectively weighting
			// the immediate neighbors at a 2x rate.
			foreach (Tile n in t.GetTilesWithinRankDistance(wc.maxRankOfWorkableTiles)) {
				score += CommercePoints * n.CommerceYield(player).yield;
				score += ShieldPoints * n.ProductionYield(player).yield;
				score += FoodPoints * n.FoodYield(player).yield;
				if (n.hasBarbarianCamp) {
					score += BarbarianCampPoints;
				}
			}

			// Give an extra bonus for rivers.
			// TODO: freshwater lakes?
			if (t.BordersRiver()) {
				score += RiverPoints;
			}

			// Try to get a rough sense of the number of surrounding land tiles
			// on the same continent, to avoid players getting stuck on a
			// peninsula.
			foreach (Tile n in t.GetTilesWithinRankDistance(4)) {
				if (n.continent == t.continent) {
					score += LandTilePoints;
				}
			}

			return score;
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
