namespace C7GameData {
	using QueryCiv3;
	using QueryCiv3.Biq;
	using System.Collections.Generic;
	using System.Linq;
	using Save;

	public class TerrainType {
		//The "key" is a language-independent name for the terrain.  Civ3 relies on their index in the list to know
		//what they are; we'll use the key.  This allows adding custom terrain types in the future, including having
		//different custom terrains in Mod A than Mod B, while still allowing internationalized versions of their
		//names that don't break the scenario.  E.g. "ocean"
		public string Key { get; set; } = "";
		//The name is the display name.  E.g. "Ocean" in English scenarios, "Hochsee" in German scenarios.
		public string DisplayName { get; set; } = "";
		public int baseFoodProduction { get; set; }
		public int baseShieldProduction { get; set; }
		public int baseCommerceProduction { get; set; }
		public int movementCost { get; set; }

		// The Civ3 TERR.RoadsBonus field, the runtime terrain record's +0x54.
		// It is the commerce a road adds on this terrain, so it is a value with
		// a magnitude rather than a flag; the original only ever tests it
		// against zero, when a city is founded (0x4ae651 reads it through
		// Tile_get_road_bonus and 0x4ae658 skips the road/railroad write when
		// it is zero). The shipped conquests.biq has 1 on the ten land terrains
		// and 0 on volcano, coast, sea and ocean.
		//
		// This is the same BIQ field the BIQ import already turns into the road
		// and railroad terrain improvements' commerce bonus (see
		// AddYieldBonusesForTerrainImprovements, which the start-location score
		// reads); it is kept on the terrain as well because the founding
		// callback reads the terrain record's field directly.
		public int roadsBonus { get; set; }

		public bool allowCities { get; set; } = true;
		public bool impassable { get; set; }

		// Terrain that is impassable to some unit types but not others, keyed by
		// unit ability. The value lists the improvements that grant a unit with
		// that ability passage, so an empty list means nothing does. In Civ3 a
		// wheeled unit may enter mountains, jungle, marsh and volcano, but only
		// once the tile is roaded or railed.
		//
		// Terrain that is impassable to every unit is the `impassable` flag
		// above instead, and no improvement lifts that.
		public Dictionary<SaveUnitPrototype.Flag, string[]> impassableTo = new();
		public StrengthBonus defenseBonus;
		public HashSet<string> allowedResources = new();
		public int height = -1;

		// The Civ3 TERR.PollutionEffect field, as read out of the BIQ's fixed
		// terrain order: the terrain this tile becomes under global warming.
		// -1 means the terrain is immune, 14 (0xE) means the tile falls back
		// to its underlying terrain, and any other value is a Civ3 terrain id
		// whose terrain must be a land terrain (the engine rejects every
		// target id of 11 or greater).
		public int pollutionEffect = -1;

		public const int UnderlyingTerrainPollutionEffect = 0xE;

		// These enum and field are kept for compatibility with CIV3 saves.
		public enum Civ3FoliageAction {
			None,
			ClearForest,
			ClearWetlands,
			PlantForest
		}
		public Civ3FoliageAction allowedFoliageAction = Civ3FoliageAction.None;

		//some stuff about graphics would probably make sense, too

		public bool isHilly() {
			if (Key.Equals("mountains") || Key.Equals("hills") || Key.Equals("volcano")) {
				return true;
			}
			return false;
		}

		public bool isVolcano() {
			return Key.Equals("volcano");
		}

		//TODO: Once we have IDs, this should *not* rely on the display name.
		//That will be after issue 58, which will be after PR 70.
		public bool isWater() {
			return Key.Equals("coast") || Key.Equals("sea") || Key.Equals("ocean");
		}

		public bool isCoast() {
			return Key.Equals("coast");
		}

		public bool isSea() {
			return Key.Equals("sea");
		}

		public bool isOcean() {
			return Key.Equals("ocean");
		}

		public override string ToString() {
			return DisplayName + "(" + baseFoodProduction + ", " + baseShieldProduction + ", " + baseCommerceProduction + ")";
		}

		public static TerrainType NONE = new TerrainType();


		public static TerrainType ImportFromCiv3(int civ3Index, TERR civ3Terrain) {
			TerrainType c7Terrain = new() {
				Key = civTerrainKeyLookup[civ3Index],
				DisplayName = civ3Terrain.Name,
				baseFoodProduction = civ3Terrain.Food,
				baseShieldProduction = civ3Terrain.Shields,
				baseCommerceProduction = civ3Terrain.Commerce,
				movementCost = civ3Terrain.MovementCost,
				roadsBonus = civ3Terrain.RoadBonus,
				allowCities = civ3Terrain.AllowCities != 0,
				impassable = civ3Terrain.Impassable != 0,
				defenseBonus = new StrengthBonus {
					description = civ3Terrain.Name,
					amount = civ3Terrain.DefenseBonus / 100.0
				},
				allowedFoliageAction = LoadFoliageAction(civ3Terrain),
				pollutionEffect = civ3Terrain.PollutionEffect,
			};

			// Civ3's only unit-specific terrain restriction. It is not absolute:
			// a road or railroad on the tile lets wheeled units through, so
			// record both the restriction and what lifts it.
			if (civ3Terrain.ImpassableByWheeled != 0)
				c7Terrain.impassableTo[SaveUnitPrototype.Flag.Wheeled] = [Tile.TileOverlays.ROAD, Tile.TileOverlays.RAILROAD];

			if (c7Terrain.Key == "mountains" || c7Terrain.Key == "volcano") {
				c7Terrain.height = 4;
			} else if (c7Terrain.Key == "hills") {
				c7Terrain.height = 2;
			} else if (c7Terrain.Key == "ocean" || c7Terrain.Key == "sea" || c7Terrain.Key == "coast" || c7Terrain.Key == "marsh") {
				c7Terrain.height = -2;
			} else if (c7Terrain.Key == "forest" || c7Terrain.Key == "jungle") {
				c7Terrain.height = 1;
			} else {
				c7Terrain.height = 0;
			}

			return c7Terrain;
		}

		private static Civ3FoliageAction LoadFoliageAction(TERR civ3Terrain) {
			if (civ3Terrain.CanChopForest) return Civ3FoliageAction.ClearForest;
			if (civ3Terrain.CanClearWetlands) return Civ3FoliageAction.ClearWetlands;
			if (civ3Terrain.CanPlantForest) return Civ3FoliageAction.PlantForest;

			return Civ3FoliageAction.None;
		}

		/// <summary>
		/// Maps a Civ3 terrain id (a TERR index) to the key OpenCiv3 uses for
		/// that terrain, or null when the id is not a known Civ3 terrain.
		/// </summary>
		public static string KeyForCiv3TerrainId(int civ3TerrainId) {
			return civTerrainKeyLookup.TryGetValue(civ3TerrainId, out string key) ? key : null;
		}

		/// <summary>
		/// The Civ3 terrain id for an OpenCiv3 terrain key, or -1 when the key
		/// does not name one of the shipped Civ3 terrains.
		/// </summary>
		public static int Civ3TerrainIdForKey(string key) {
			foreach (KeyValuePair<int, string> entry in civTerrainKeyLookup) {
				if (entry.Value == key) {
					return entry.Key;
				}
			}
			return -1;
		}

		//This only works for Conquests due to the new terrains being added in the middle of the list.
		private static Dictionary<int, string> civTerrainKeyLookup = new Dictionary<int, string>() {
			{ 0,  "desert"},
			{ 1,  "plains"},
			{ 2,  "grassland"},
			{ 3,  "tundra"},
			{ 4,  "flood plain"},
			{ 5,  "hills"},
			{ 6,  "mountains"},
			{ 7,  "forest"},
			{ 8,  "jungle"},
			{ 9,  "marsh"},
			{ 10, "volcano"},
			{ 11, "coast"},
			{ 12, "sea"},
			{ 13, "ocean"}
		};
	}
}
