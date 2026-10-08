using System.Collections.Generic;
using System.Linq;
using System;
using C7GameData;

namespace C7Engine.Pathing {
	public class TradeNetworkSegment {
		public Dictionary<Resource, int> resourceCounts = new();
		public HashSet<Tile> tiles = new();

		public void AddTile(Tile t, Player p) {
			// A tile can be reached by more than one pass (a road tile that is
			// also an endpoint of a sea route, or two merged components), so
			// only count it the first time it joins the segment.
			if (!tiles.Add(t)) {
				return;
			}
			if (t.Resource != Resource.NONE && t.OwningPlayer() == p) {
				if (resourceCounts.TryGetValue(t.Resource, out int currentCount)) {
					resourceCounts[t.Resource] = currentCount + 1;
				} else {
					resourceCounts[t.Resource] = 1;
				}
			}
		}
	}

	// A class for calculating the trade networks.
	//
	// The main idea is that calculating the entire empire's trade network once
	// is faster than doing it repeatedly for each city. By doing it once we can
	// do a simple flood fill of the road network, rather than needing to do
	// actual pathfinding between different tiles.
	//
	// Like Civ3, the network is built in three passes over the city list: land
	// (roads), then air (airports), then water (harbours). Each pass merges the
	// components it connects, so a city's segment is the union of every route
	// that reaches it.
	//
	// TODO: Account for passing through the borders of civs we're at war with
	// on land.
	// TODO: Invalidate the trade network when war status changes.
	public class TradeNetwork {
		private Dictionary<Player, Dictionary<City, TradeNetworkSegment>> segments = new();
		private GameData gameData;

		public TradeNetwork(GameData gameData) {
			this.gameData = gameData;
			foreach (Player p in gameData.players) {
				ComputeTradeNetwork(p);
			}
		}

		private void ComputeTradeNetwork(Player player) {
			segments[player] = new();
			ComputeLandNetwork(player);
			ComputeAirNetwork(player);
			ComputeWaterNetwork(player);
		}

		// Pass 0: the road network.
		private void ComputeLandNetwork(Player player) {
			HashSet<Tile> seen = new();

			foreach (City c in player.cities) {
				if (segments[player].ContainsKey(c)) {
					continue;
				}

				// If we don't know about this city yet, start a new network
				// segment and do a flood fill for all roads coming from the
				// city.
				TradeNetworkSegment segment = new();
				segments[player][c] = segment;

				Queue<Tile> toCheck = new();
				toCheck.Enqueue(c.location);
				seen.Add(c.location);

				while (toCheck.Count > 0) {
					Tile x = toCheck.Dequeue();
					segment.AddTile(x, player);

					if (x.cityAtTile != null) {
						segments[player][x.cityAtTile] = segment;
					}

					foreach (Tile n in x.neighbors.Values) {
						if (n.IsRoaded() && seen.Add(n)) {
							toCheck.Enqueue(n);
						}
					}
				}
			}
		}

		// Pass 1: air trade. An airport joins a city to every city of a civ it
		// is not at war with whose tile the acting player has revealed, with no
		// distance limit and without any pathfinding.
		private void ComputeAirNetwork(Player player) {
			List<City> cities = gameData.cities;
			for (int i = 0; i < cities.Count; i++) {
				City a = cities[i];
				if (!CanTradeViaAir(a)) {
					continue;
				}

				for (int j = i + 1; j < cities.Count; j++) {
					City b = cities[j];
					if (!CanTradeViaAir(b)) {
						continue;
					}
					if (IsAtWar(a.owner, b.owner)) {
						continue;
					}
					if (!player.HasExploredTile(b.location)) {
						continue;
					}
					if (AreConnected(player, a, b)) {
						continue;
					}

					Merge(player, a, b);
				}
			}
		}

		// Pass 2: sea trade. Two harbour cities connect when a water-only path
		// joins them, gated by the sea and ocean trade advances (or the Great
		// Lighthouse, for Sea tiles only).
		private void ComputeWaterNetwork(Player player) {
			List<City> cities = gameData.cities;
			for (int i = 0; i < cities.Count; i++) {
				City a = cities[i];
				if (!CanTradeViaWater(a)) {
					continue;
				}

				for (int j = i + 1; j < cities.Count; j++) {
					City b = cities[j];
					if (!CanTradeViaWater(b)) {
						continue;
					}
					if (IsAtWar(a.owner, b.owner)) {
						continue;
					}
					if (AreConnected(player, a, b)) {
						continue;
					}
					if (!FindSeaPath(player, a.location, b.location, out List<Tile> path)) {
						continue;
					}

					// The sea route is part of the network too, so resources
					// on the water tiles it crosses (whales, fish) accrue to
					// the cities it connects.
					TradeNetworkSegment segment = GetOrCreateSegment(player, a);
					foreach (Tile t in path) {
						segment.AddTile(t, player);
					}

					Merge(player, a, b);
				}
			}
		}

		// A city can trade over the water when it owns an active building that
		// allows it (a Harbour), and likewise through the air (an Airport).
		private static bool CanTradeViaWater(City c) {
			return HasActiveBuilding(c, b => b.allowsWaterTrade);
		}

		private static bool CanTradeViaAir(City c) {
			return HasActiveBuilding(c, b => b.allowsAirTrade);
		}

		private static bool HasActiveBuilding(City c, Func<Building, bool> hasFlag) {
			foreach (CityBuilding cb in c.GetBuildings()) {
				if (hasFlag(cb.building) && !IsObsolete(cb.building, c.owner)) {
					return true;
				}
			}
			return false;
		}

		private static bool IsObsolete(Building b, Player owner) {
			return b.renderedObsoleteBy != null && owner.knownTechs.Contains(b.renderedObsoleteBy.id);
		}

		private static bool IsAtWar(Player a, Player b) {
			return PlayerRelationship.AtWar(a, b);
		}

		// The Great Lighthouse lets its owner cross Sea tiles without knowing
		// an EnablesTradeOverSea advance. It expires with the advance recorded
		// as its obsolete-by tech, so only active wonders count.
		private bool OwnsSafeSeaTravelWonder(Player player) {
			foreach (Tuple<City, CityBuilding> wonder in player.GetActiveWonders()) {
				if (wonder.Item2.building.safeSeaTravel) {
					return true;
				}
			}
			return false;
		}

		private bool KnowsTradeAdvance(Player player, bool overSea) {
			foreach (Tech tech in gameData.techs) {
				if (!player.knownTechs.Contains(tech.id)) {
					continue;
				}
				if (overSea ? tech.EnablesTradeOverSea : tech.EnablesTradeOverOcean) {
					return true;
				}
			}
			return false;
		}

		// A Sea tile may only be entered with an EnablesTradeOverSea advance or
		// the Great Lighthouse; an Ocean tile needs EnablesTradeOverOcean. All
		// other water (and the land tiles of the endpoint cities) are free.
		private bool CanEnterWaterTerrain(Player player, TerrainType terrain) {
			if (terrain.isSea()) {
				return KnowsTradeAdvance(player, overSea: true) || OwnsSafeSeaTravelWonder(player);
			}
			if (terrain.isOcean()) {
				return KnowsTradeAdvance(player, overSea: false);
			}
			return true;
		}

		// Breadth-first search over water tiles. The only land tiles a sea
		// route may use are its two endpoint cities; everything between them
		// must be water.
		//
		// Every step must run through tiles the acting player has revealed and
		// must not end on the territory of a civ the player is at war with.
		private bool FindSeaPath(Player player, Tile start, Tile end, out List<Tile> path) {
			path = new List<Tile>();
			if (start == end) {
				return false;
			}

			Dictionary<Tile, Tile> cameFrom = new();
			HashSet<Tile> seen = new() { start };
			Queue<Tile> toCheck = new();
			toCheck.Enqueue(start);

			while (toCheck.Count > 0) {
				Tile x = toCheck.Dequeue();
				foreach (Tile n in x.neighbors.Values) {
					if (n == null || n == Tile.NONE || seen.Contains(n)) {
						continue;
					}

					bool isEnd = n == end;
					if (!n.IsWater() && !isEnd) {
						continue;
					}
					if (!SeaStepAllowed(player, x, n)) {
						continue;
					}

					seen.Add(n);
					cameFrom[n] = x;
					if (isEnd) {
						for (Tile t = n; t != start; t = cameFrom[t]) {
							path.Add(t);
						}
						path.Add(start);
						path.Reverse();
						return true;
					}

					toCheck.Enqueue(n);
				}
			}
			return false;
		}

		private bool SeaStepAllowed(Player player, Tile from, Tile to) {
			// Unlike the land pass, the sea pass always requires the tiles to
			// have been revealed by the acting player.
			if (!player.HasExploredTile(from) || !player.HasExploredTile(to)) {
				return false;
			}

			// A route may not cross into the territory of a civ at war with
			// the acting player.
			Player owner = to.OwningPlayer();
			if (owner != null && IsAtWar(player, owner)) {
				return false;
			}

			return CanEnterWaterTerrain(player, from.baseTerrainType) &&
				CanEnterWaterTerrain(player, to.baseTerrainType);
		}

		private TradeNetworkSegment GetOrCreateSegment(Player player, City city) {
			if (!segments[player].TryGetValue(city, out TradeNetworkSegment segment)) {
				segment = new TradeNetworkSegment();
				segments[player][city] = segment;
			}
			return segment;
		}

		// Whether the two cities share a component in the given player's trade
		// network (roads, airports or sea routes).
		public bool AreConnected(Player player, City a, City b) {
			return segments[player].TryGetValue(a, out TradeNetworkSegment aSegment) &&
				segments[player].TryGetValue(b, out TradeNetworkSegment bSegment) &&
				aSegment == bSegment;
		}

		// Merges two cities' components, so that the union of the routes that
		// reach either one reaches both.
		private void Merge(Player player, City a, City b) {
			TradeNetworkSegment target = GetOrCreateSegment(player, a);
			TradeNetworkSegment source = GetOrCreateSegment(player, b);
			if (target == source) {
				return;
			}

			foreach (Tile t in source.tiles) {
				target.AddTile(t, player);
			}
			foreach (City c in segments[player].Keys.ToList()) {
				if (segments[player][c] == source) {
					segments[player][c] = target;
				}
			}
		}

		// Returns the resources and their counts that are available to the given
		// city.
		public Dictionary<Resource, int> GetResourcesAvailableToCity(Player player, City city) {
			TradeNetworkSegment segment = segments[player][city];
			Dictionary<Resource, int> result = new();
			foreach ((Resource r, int count) in segment.resourceCounts) {
				if (player.KnowsAboutResource(r)) {
					result[r] = count;
				}
			}
			return result;
		}

		public bool HasTradeAccess(Tile t, Player p, Resource r) {
			if (!p.KnowsAboutResource(r)) {
				return false;
			}

			foreach (TradeNetworkSegment segment in segments[p].Values) {
				if (segment.tiles.Contains(t) && segment.resourceCounts.TryGetValue(r, out int count) && count > 0) {
					return true;
				}
			}
			return false;
		}

		public bool ConnectedToCapital(Player p, City c) {
			return segments[p][c] == segments[p][p.cities[0]];
		}
	}
}
