using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Serilog;
using C7Engine.Lua;
using C7Engine.Pathing;
using System.Threading.Tasks;
using C7Engine;

[assembly: InternalsVisibleTo("EngineTests")]
namespace C7GameData {
	public class GameData {
		private static ILogger log = Log.ForContext<GameData>();

		public int seed = -1;   //change here to set a hard-coded seed
		public int turn { get; set; }

		// How many nuclear weapons have been used in this game. Civ3 tracks
		// this in a global and adds its square to the pollution that drives
		// global warming (18_terrain_improvement.md section 7 step 1). Nothing
		// in OpenCiv3 launches a nuclear weapon yet, so nothing increments this
		// and the nukes^2 term of AccumulatedPollution is always zero. That is a
		// known consequence of the missing nuclear-launch path, not a defect in
		// the warming rule, which is implemented in full.
		public int nukesUsed = 0;

		// Civ3's UI-only global-warming severity indicator (0xa5269c), 0-3, as
		// recomputed by Pollution.DoPerTurnGlobalWarming. No UI consumes it yet.
		public int globalWarmingSeverity = 0;
		public static Random rng; // TODO: Is GameData really the place for this?
		public ID.Factory ids = new();
		public GameMap map { get; set; }
		public List<Player> players = new List<Player>();
		public List<TerrainType> terrainTypes = new List<TerrainType>();
		public List<TerrainImprovement> terrainImprovements = [];
		public List<Resource> Resources = new List<Resource>();
		public List<MapUnit> mapUnits { get; set; } = new List<MapUnit>();
		public List<UnitPrototype> unitPrototypes = new();
		public List<Building> Buildings = new();
		public List<Inflow> Inflows = new();

		// The names of all great wonders that have been built.
		public HashSet<string> GreatWondersBuilt = new();

		public List<City> cities = new List<City>();

		internal List<Civilization> civilizations = new List<Civilization>();
		internal HashSet<CultureGroup> cultureGroups = new HashSet<CultureGroup>();
		internal HashSet<Alliance> alliances;
		internal Dictionary<Alliance, Alliance> allianceWars = new Dictionary<Alliance, Alliance>();

		public List<ExperienceLevel> experienceLevels = new List<ExperienceLevel>();
		// The rules' era list, in order. A BIQ game reads it from the ERAS
		// section; a game with no BIQ reads it from ruleset.json. Everything
		// that used to assume Civ3's four eras goes through this list.
		public List<Era> eras = new();
		public List<Tech> techs = new();
		public List<CitizenType> citizenTypes = new();
		public List<Terraform> Terraforms = new();
		public List<Government> governments = new();
		public List<EspionageMission> espionageMissions = new();
		public List<CultureLevel> cultureLevels = new();
		public List<WorldSize> worldSizes = new();
		public List<Difficulty> difficulties = new();
		public Difficulty gameDifficulty = new();
		public string defaultExperienceLevelKey;
		public ExperienceLevel defaultExperienceLevel;
		public Rules rules;
		public TimeOptions timeOptions;
		public Dictionary<string, List<HistTurnRecord>> history;

		public VictoryConditions victoryConditions;
		public List<IVictory> victories = new();
		public bool gameOver;
		public Player winner;

		/// Whether the rules turn on the victory-point group (spec 25 section 2.8's
		/// 0x26000 mask). Victory points are accumulated and the type-8 condition is
		/// evaluated only when this is true.
		public bool VictoryPointsEnabled => victoryConditions is not null && victoryConditions.VictoryPointsEnabled;
		// TODO: Victory type serialization

		public BarbarianInfo barbarianInfo = new BarbarianInfo();

		/// <summary>
		/// The civilization whose city-name list is the barbarian tribe table
		/// (spec 22 section 2): RACE 0, "A Barbarian Chiefdom". Its 76 city
		/// names are the tribe names in culture-group bands of 15 plus the
		/// slot-75 fallback.
		/// </summary>
		public Civilization BarbarianCivilization => civilizations.Find(c => c.isBarbarian);

		/// <summary>
		/// The next free tribe slot of a culture group's band, held slots being
		/// the tribes the camps on the map currently hold.
		/// </summary>
		public int PickBarbarianTribeSlot(int cultureGroupIndex) {
			return BarbarianTribes.PickTribeSlot(cultureGroupIndex, BarbarianTribes.HeldSlots(map));
		}

		/// <summary>
		/// The name of a tribe slot, or null when the loaded rules carry no name
		/// there.
		/// </summary>
		public string BarbarianTribeName(int slot) {
			return BarbarianTribes.TribeName(this, slot);
		}

		// The global "the map has been revealed" flag of
		// 22_barbarians_goody_huts.md section 5: the Maps goody-hut outcome records
		// here that it has changed what its player can see.
		//
		// This is NOT the same engine field as the scenario loader's reveal-all
		// mode of 28_formats.md section 2.3. They are two different bytes in the
		// original: the Maps outcome writes 0x00a281c5, a map-view invalidation
		// bit in the 0x00a281c4..c6 cluster that every reveal helper and the map
		// view module also write, while the loader flag is BIC +0xba0, read once
		// on the game-start path by Map_process_after_placing. They cannot be one
		// field either: the Maps outcome reveals only the hut's neighbourhood
		// (section 4.5), while the loader flag means "every tile is revealed".
		//
		// The original contains no reader of the Civ3 byte: its consumer
		// is the map/minimap redraw, so the engine keeps the flag as the record of
		// the reveal without deriving anything from it.
		public bool mapHasBeenRevealed = false;

		public StrengthBonus fortificationBonus;
		public StrengthBonus riverCrossingBonus;
		public StrengthBonus cityLevel1DefenseBonus;
		public StrengthBonus cityLevel2DefenseBonus;
		public StrengthBonus cityLevel3DefenseBonus;

		public int healRateInFriendlyField;
		public int healRateInNeutralField;
		public int healRateInHostileField;
		public int healRateInCity;

		public bool observerMode = false;
		public bool showGridCoordinates = false;

		public string scenarioSearchPath;   //legacy from Civ3, we'll probably have a more modern format someday but this keeps legacy compatibility

		internal BehaviorEngine luaBehaviorEngine;
		internal GameMode.Config gameModeConfig;

		// The cached trade network for all players. This is invalidated whenever
		// a road is built or a city is created/destroyed.
		private TradeNetwork tradeNetwork;

		// An action called after initialization of EngineStorage
		internal Action onGameCreation;

		public GameData(int customSeed = -1) {
			map = new GameMap();
			seed = customSeed;
			// this will probably never happen, leaving it as a fallback
			if (seed == -1) {
				log.Information("Random seed was not set, generating...");
				rng = new Random();
				seed = rng.Next(int.MaxValue);
			}
			rng = new Random(seed);
			log.Information($"Seed is {seed}");
		}

		// Returns the first human player in the set of players, or the first
		// non-barbarian player if we're in observer mode (where the human player
		// is no longer marked as human).
		public Player GetFirstHumanPlayer() {
			foreach (Player p in players) {
				if (p.isHuman) {
					return p;
				}
			}

			if (observerMode) {
				foreach (Player p in players) {
					if (!p.isBarbarians) {
						return p;
					}
				}
			}

			return null;
		}

		public List<Player> GetRivals(Player player) {
			return players.Where(p => !p.isBarbarians && p != player && !p.defeated).ToList();
		}

		public List<Player> GetKnownRivals(Player player) {
			var rivals = GetRivals(player);
			return rivals.Where(x => player.playerRelationships.ContainsKey(x.id)).ToList();
		}

		public MapUnit GetUnit(ID id) {
			return mapUnits.Find(u => u.id == id);
		}

		public Player GetPlayer(ID id) {
			return players.Find(p => p.id == id);
		}

		// Civ3's repeatable future technology (16_science.md §3.1). It is not
		// part of the tech tree and is never recorded in a player's known
		// techs: once every real technology is known a player researches this
		// instead, and each completion increments the player's future-tech
		// counter, which the score's technology term reads. The id is stable so
		// that a save made mid-research reloads with the same target.
		public static readonly ID FutureTechId = ID.FromString("future-technology-0");

		// A fresh instance each time, so a rules change (or a rules load that
		// happens after the first read) is always reflected in the cost.
		public Tech FutureTech => new() {
			id = FutureTechId,
			Name = "Future Technology",
			Cost = rules?.FutureTechCost ?? 0,
		};

		public bool IsFutureTech(Tech tech) {
			return tech != null && tech.id == FutureTechId;
		}

		public Tech GetTech(ID id) {
			if (id == FutureTechId) {
				return FutureTech;
			}
			return techs.Find(p => p.id == id);
		}

		// Returns the espionage mission with the given id (its position in the
		// BIQ ESPN table), or null when the id is out of range.
		public EspionageMission GetEspionageMission(int id) {
			if (espionageMissions == null || id < 0 || id >= espionageMissions.Count) {
				return null;
			}
			return espionageMissions[id];
		}

		public ExperienceLevel GetExperienceLevelAfter(ExperienceLevel experienceLevel) {
			int n = experienceLevels.IndexOf(experienceLevel);
			if (n + 1 < experienceLevels.Count)
				return experienceLevels[n + 1];
			else
				return null;
		}

		// Players are in an active locked war (can't make peace)
		private bool AreAlliancesInLockedWar(Alliance one, Alliance other) {
			return allianceWars.Any(
				kvp => (one != null && other != null) &&
					((kvp.Key.name == one.name && kvp.Value.name == other.name)
					|| (kvp.Key.name == other.name && kvp.Value.name == one.name)));
		}
		public bool AreInLockedWar(Player one, Player other) {
			return AreAlliancesInLockedWar(one.alliance, other.alliance) && one != other;
		}

		// Both players are part of the same alliance (can't declare war)
		// We exclude ourselves from this.
		private bool AreInSameAlliance(Alliance one, Alliance other) {
			return one != null && other != null && one == other;
		}
		public bool AreInLockedPeace(Player one, Player other) {
			return AreInSameAlliance(one.alliance, other.alliance) && one != other;
		}

		/// <summary>
		/// Recomputes the owner of every tile, and runs the side effects of an
		/// ownership assignment (spec 17 sections 5 and 5.1): a tile that is
		/// GIVEN a non-zero owner disperses a barbarian camp on it and forces a
		/// goody hut on it to become a city of that owner. A tile whose owner is
		/// cleared runs neither handler, which is why the clearing half stays a
		/// plain store (see <see cref="UpdateTileOwnersOnCityDestruction"/>).
		/// </summary>
		/// <param name="previousOwners">
		/// The owner a city's tiles held before the city changed hands, for a
		/// city whose owner changed since those tiles were last assigned. Civ3
		/// compares the tile's stored owner against the owner it is about to
		/// write (entry 0x5d3ab0; the equality test is at 0x5d3add), and the one
		/// way a live city changes hands - a capture - rewrites the owner of
		/// every tile the city already holds. OpenCiv3 derives a tile's owner
		/// from its own city, so without this the sweep cannot see that a
		/// capture changed those tiles' owner at all.
		///
		/// <para>A raze is the one case this cannot express exactly: the
		/// destruction path clears its tiles to no owner before sweeping
		/// (spec 17 section 5.1's clearing writes, which run no handler), so a
		/// tile a surviving city of the *same* civ then takes over reads as a
		/// fresh assignment here, where Civ3 compares the tile's unchanged owner
		/// byte and writes nothing. The difference needs a feature to have
		/// survived inside the razed city's borders, which the assigning path
		/// itself consumes at the first owner change.</para>
		/// </param>
		public void UpdateTileOwners(IReadOnlyDictionary<City, Player> previousOwners = null) {
			// We do this at the end of the method - we don't need to do this
			// for each tile we add in the loop below.
			bool recomputeActiveTiles = false;

			// The tiles this pass gives a new owner to. The handlers themselves
			// run once the pass is over: converting a hut founds a city, which
			// runs this method again, and running that nested pass in the middle
			// of the loops below would rewrite the ownership they are still
			// working through and mutate the city list they are walking.
			Dictionary<Tile, Player> ownershipChanges = new();

			foreach (City city in cities) {
				if (city.residents.Count == 0) {
					continue; // skip destroyed cities
				}

				AssignTileOwner(city.location, city, recomputeActiveTiles, previousOwners, ownershipChanges);

				foreach (Tile t in city.GetTilesWithinBorders()) {
					// If another city has claim to this tile, we need to resolve
					// that conflict.
					if (t.owningCity != null && ResolveTileOwnershipConflict(t.owningCity, city, t, out City winnerCity)) {
						AssignTileOwner(t, winnerCity, recomputeActiveTiles, previousOwners, ownershipChanges);
						continue;
					}

					AssignTileOwner(t, city, recomputeActiveTiles, previousOwners, ownershipChanges);
				}
			}

			foreach (Player player in players) {
				player.tileKnowledge.RecomputeActiveTiles();
				player.UpdateResourcesInBorders(map.tiles.Where(t => t.owningCity?.owner == player));

				foreach (Tile t in player.tileKnowledge.knownTiles.Where(t => t.owningCity == null && t.GetEdgeNeighbors().Any(e => e.owningCity != null)).ToList()) {
					// Law VII
					TryResolveOpposingNeighbors(t, TileDirection.NORTHWEST, TileDirection.SOUTHEAST, previousOwners, ownershipChanges);
					if (t.owningCity != null) continue;
					// Law VIII
					TryResolveOpposingNeighbors(t, TileDirection.NORTHEAST, TileDirection.SOUTHWEST, previousOwners, ownershipChanges);
				}
			}

			foreach ((Tile tile, Player receiver) in ownershipChanges) {
				// A handler can found a city whose own sweep takes this tile away
				// again, so the owner is re-read: only the owner the tile actually
				// ended up with runs its handlers. Each handler clears the feature
				// it consumes before it acts, so no tile converts twice.
				if (tile.owningCity?.owner != receiver) {
					continue;
				}

				RunOwnershipAssignmentSideEffects(tile, receiver);
			}
		}

		/// <summary>
		/// Gives a tile to a city. Civ3 routes every ownership assignment through
		/// one writer (spec 17 sections 5 and 5.1), so all of the sweep's
		/// assignments go through here, and each records the change for the side
		/// effects to run once the pass is over. An assignment that leaves the
		/// owner as it was records nothing.
		/// </summary>
		private void AssignTileOwner(Tile tile, City city, bool recomputeActiveTiles,
				IReadOnlyDictionary<City, Player> previousOwners, Dictionary<Tile, Player> ownershipChanges) {
			// The owner the tile holds going in. For a city that changed hands
			// since its tiles were last assigned, that is the owner it had then,
			// not the one its city carries now.
			Player storedOwner = tile.owningCity?.owner;
			if (tile.owningCity != null && previousOwners != null && previousOwners.TryGetValue(tile.owningCity, out Player formerOwner)) {
				storedOwner = formerOwner;
			}

			tile.owningCity = city;
			city.owner.tileKnowledge.AddTilesToKnown(tile, recomputeActiveTiles);

			if (storedOwner != city.owner) {
				ownershipChanges[tile] = city.owner;
			}
		}

		/// <summary>
		/// The two side effects Civ3's owner writer runs when a tile is given a
		/// non-zero owner (spec 17 sections 5 and 5.1; 22 sections 4.6 and 6.3):
		/// a barbarian camp on the tile is dispersed, and a goody hut on it
		/// forces the City outcome, so the hut becomes a city of the civ that
		/// took the tile. The camp runs first, which is the writer's own order
		/// (the camp call is at 0x5d3ded and the hut call at 0x5d3e2b), and the
		/// receiver of both is the new owner, never a unit.
		/// </summary>
		private void RunOwnershipAssignmentSideEffects(Tile tile, Player receiver) {
			if (tile.hasBarbarianCamp) {
				BarbarianCampInteractions.DisperseCamp(this, tile, receiver);
			}
			if (tile.hasGoodyHut) {
				GoodyHutInteractions.ConsumeFromTerritoryChange(this, receiver, tile);
			}
		}

		private void TryResolveOpposingNeighbors(Tile t, TileDirection dirA, TileDirection dirB,
				IReadOnlyDictionary<City, Player> previousOwners, Dictionary<Tile, Player> ownershipChanges) {
			if (!t.neighbors.TryGetValue(dirA, out Tile a) || !t.neighbors.TryGetValue(dirB, out Tile b)) return;
			if (a.owningCity == null || b.owningCity == null) return;
			if (a.owningCity.owner != b.owningCity.owner) return;
			if (!ResolveTileOwnershipConflict(a.owningCity, b.owningCity, t, out City winnerCity)) return;

			// Law II
			if (t.baseTerrainType.Key == "ocean" && t.RankDistanceTo(winnerCity.location) > 2) {
				// An owner clear, so no side effect runs here.
				t.owningCity = null;
				return;
			}
			// This neighbour resolution is a phase of the same sweep, so its
			// assignment follows the assigning path too. It keeps the default
			// recomputeActiveTiles of its old direct call.
			AssignTileOwner(t, winnerCity, true, previousOwners, ownershipChanges);
		}

		public void UpdateTileOwnersOnCityDestruction(City city) {
			city.location.owningCity = null;

			var borderTiles = city.GetTilesWithinBorders();
			var borderTileIds = borderTiles.Select(x => x.Id).ToHashSet();

			foreach (Tile tile in borderTiles) {
				tile.owningCity = null;

				// Aggressively remove ownership from edge neighbors around the city outside the natural border.
				// This clears out tile ownership due to Law VII or Law VIII.
				// Regular tile ownership update will re-assign ownership where needed.
				foreach (var fringeTile in tile.GetEdgeNeighbors().Where(x => !borderTileIds.Contains(x.Id))) {
					if (fringeTile.HasCity()) // skip encountered cities, just in case
						continue;

					fringeTile.owningCity = null;
				}
			}

			UpdateTileOwners();
		}

		public void CheckForCivDestructionAndNotifyUi(Player player) {
			if (this.CheckForCivDestruction(player)) {
				this.CivDestructionCallback(player);
				// Let the UI know about the civ destruction.
				new MsgCivilizationDestroyed(player.civilization).send();
			}
		}

		/// <summary>
		/// Eliminates a player outright: the instant-elimination and regicide
		/// endings (spec 25 section 4, which the original decides in the
		/// city-loss path). The player's remaining cities are destroyed, its
		/// units are removed and its relationships are cleared - the same
		/// teardown the ordinary "no cities and no settlers" destruction
		/// performs, but without waiting for the player to run out of
		/// cities.
		/// </summary>
		public void EliminatePlayer(Player loser) {
			if (loser == null || loser.defeated) {
				return;
			}
			// Mark defeated first so the per-city loss checks inside the
			// destruction loop cannot re-enter elimination.
			loser.defeated = true;
			foreach (City city in loser.cities.ToList()) {
				CityInteractions.DestroyCity(city.location);
			}
			CivDestructionCallback(loser);
			new MsgCivilizationDestroyed(loser.civilization).send();
		}

		private bool CheckForCivDestruction(Player player) {
			// TODO: Implement the full set of conditions for destroying a civ;
			// handling cases like 1 city elimination, regicide, settlers that
			// are still alive, etc.
			if (player.isBarbarians) {
				return false;
			}
			// Already eliminated (e.g. by instant elimination), so the units
			// and relationships are gone and the notification has been sent.
			if (player.defeated) {
				return false;
			}
			if (player.RemainingCities() > 0) {
				return false;
			}
			if (player.units.Any(u => u.unitType.isSettler)) {
				return false;
			}

			return true;
		}

		private void CivDestructionCallback(Player player) {
			// Mark player as defeated, so when we start removing units,
			// we don't have to check each time if the civ is destroyed
			player.defeated = true;

			// Start from the end and start deleting backwards
			// because the other way doesn't actually go through all units
			// probably because we keep modifying the list and its count gets all messed up
			for (int i = player.units.Count - 1; i >= 0; --i) {
				RemoveUnit(player.units[i]);
			}

			// Remove this civ from all other player's relationships.
			foreach (Player p in players) {
				p.playerRelationships.Remove(player.id);
			}
		}

		[LuaMethod]
		public void SendMessageToUiFromLua(string message, Tile location) {
			log.Information($"[LUA API] - {message}");
			new MsgShowTemporaryPopup(message, location).send();

		}

		public async Task DisbandUnit(MapUnit unit) {
			log.Information($"Player {unit.owner} disbands unit: {unit}");

			var disbandFunction = this.luaBehaviorEngine.ImportFunc<Action<MapUnit>>("gameplay.units.disband");
			disbandFunction.Invoke(unit);

			await unit.animateAsync(MapUnit.AnimatedAction.DEATH, AnimationEnding.Pause);

			RemoveUnit(unit);
		}

		internal void RemoveUnit(MapUnit unit) {
			// An army takes its members with it. The cascade is recursive so that a
			// member's own cargo goes as well.
			List<MapUnit> members = unit.IsArmy ? unit.Members() : [];

			// Set unit's hit points to zero to indicate that it's no longer alive. Ultimately we may not want to do this. I'm only doing it right
			// now since this way all the UI needs to do to check if the selected unit has been destroyed is to check its hit points.
			unit.hitPointsRemaining = 0;
			unit.movementPoints.onConsumeAll();

			if (unit.currentAI != null) {
				unit.currentAI.UpdateOnDeath();
				unit.currentAI = null;
			}

			// EngineStorage.animTracker.endAnimation(unit, false);   TODO: Must send message instead of call directly
			unit.location.unitsOnTile.Remove(unit);
			mapUnits.Remove(unit);

			Player owner = unit.owner;
			owner.units.Remove(unit);

			log.Information($"Player {owner} removed unit: {unit}");

			// Probably could be optimized, by checking if this unit had radar,
			// or if there are other units on the tile,
			// but I don't want to this prematurely here,
			// since there might be things I am taking into account,
			// and end up introducing a bunch of bugs.
			// If it ends up being a problem, we could certainly look into this more.
			owner.tileKnowledge.RecomputeActiveTiles();

			foreach (MapUnit member in members) {
				RemoveUnit(member);
			}

			if (!owner.defeated)
				CheckForCivDestructionAndNotifyUi(unit.owner);
		}

		internal void SpawnUnit(Player player, UnitPrototype proto, Tile tile) {
			SpawnUnit(player, proto, tile, BarbarianTribes.None);
		}

		/// <summary>
		/// Spawns a unit, attaching a barbarian tribe when there is one. A
		/// barbarian unit spawned without an explicit tribe takes the fallback
		/// slot, which is what Civ3's own unit constructor does when it is handed
		/// owner 0 (the barbarian player) and tribe -1: every barbarian unit
		/// carries a tribe, and the name of an unattributed one is "Barbarian".
		/// </summary>
		internal void SpawnUnit(Player player, UnitPrototype proto, Tile tile, int barbarianTribeId) {
			// TODO: consolidate unit spawning routines (here)

			var defaultExpLevel = this.defaultExperienceLevel;
			var barbExpLevel = this.experienceLevels.First(e => e.baseHitPoints == this.barbarianInfo.maxHitpoints);

			MapUnit newUnit = proto.GetInstance(this.GenerateID(proto.name), proto, player, location: tile);

			newUnit.experienceLevel = player.isBarbarians ? barbExpLevel : defaultExpLevel;
			newUnit.experienceLevelKey = player.isBarbarians ? barbExpLevel.key : defaultExpLevel.key;
			newUnit.hitPointsRemaining = player.isBarbarians ? barbExpLevel.baseHitPoints : defaultExpLevel.baseHitPoints;
			newUnit.barbarianTribeId = player.isBarbarians && barbarianTribeId == BarbarianTribes.None
				? BarbarianTribes.FallbackSlot
				: barbarianTribeId;

			tile.unitsOnTile.Add(newUnit);
			this.mapUnits.Add(newUnit);
			player.units.Add(newUnit);

			log.Debug("New unit of type {type} added at {tile} for player {player}",
				proto.name, tile, player);
		}

		public int TechCostFor(Tech tech, Player player) {
			// Cost formula from https://forums.civfanatics.com/threads/research-cost-formula-v1-29f.29485/.
			// Research Cost = [MM * [10*COST * (1 - N/[CL*1.75])]/(CF * 10)] - progress
			//
			// MM = map modifier (tiny=160, small=200, standard=240, large=320, huge=400)
			// COST = tech cost
			// CF = difficulty factor
			// N = number of known civs that have discovered the tech
			// CL = civs left in game
			//
			// We also have the min/max turns to research of 4 and 50 (defined
			// in the rules)
			// TODO: See this this whole equation can be configurable
			int knownCivsThatKnowTheTech = 0;
			int civsLeft = 0;
			foreach (Player p in players) {
				if (player.playerRelationships.ContainsKey(p.id) && p.knownTechs.Contains(tech.id)) {
					++knownCivsThatKnowTheTech;
				}
				if (!p.isBarbarians && !p.defeated) {
					++civsLeft;
				}
			}

			int difficultyFactor = player.isHuman ? gameDifficulty.HumanCostFactor : gameDifficulty.AiCostFactor;
			float knowledgeFactor = 1.0f - knownCivsThatKnowTheTech / (civsLeft * 1.75f);
			float researchCost = map.techRate * 10 * tech.Cost  * knowledgeFactor/ (difficultyFactor * 10);

			// Only include the progress factor if this is the tech actively
			// being researched.
			if (player.currentlyResearchedTech == tech.id) {
				researchCost -= player.beakers;
			}

			return (int)Math.Max(Math.Floor(researchCost), 0);
		}

		public TradeNetwork GetTradeNetwork() {
			if (tradeNetwork == null) {
				tradeNetwork = new(this);
			}
			return tradeNetwork;
		}

		public void InvalidateCachedTradeNetwork() {
			tradeNetwork = null;
		}

		/// <summary>
		/// Reveal every tile to every player in the game.
		///
		/// This is the reveal-all pass of 28_formats.md section 3.2: with a
		/// non-zero argument Map_process_after_placing walks every tile and calls
		/// Leader_reveal_tile for each player whose liveness bit is set, rather
		/// than only the tiles that are already marked revealed. The scenario
		/// loader runs it for a file that has no per-tile reveal data of its own
		/// (section 2.3), which is what SaveGame.revealAllTilesOnLoad carries
		/// across from the read to the game start.
		/// </summary>
		public void RevealAllTiles() {
			foreach (Player player in players) {
				// Defeated players are the ones whose liveness bit is clear.
				if (player.defeated) {
					continue;
				}
				player.tileKnowledge.RevealAllTiles(map);
			}
		}

		// Rules taken from https://forums.civfanatics.com/threads/the-eight-laws-of-border-dynamics.106882/
		private bool ResolveTileOwnershipConflict(City a, City b, Tile t, out City owner) {
			owner = null;
			if (a.Equals(b)) { owner = a; return true; }

			int aRank = a.location.RankDistanceTo(t);
			int bRank = b.location.RankDistanceTo(t);

			// Law I
			// Cities can claim tiles of rank n+1, where n is the city's expansion level
			if (a.GetBorderExpansionLevel() + 1 < aRank && b.GetBorderExpansionLevel() + 1 >= bRank) { owner = b; return true; }
			if (b.GetBorderExpansionLevel() + 1 < bRank && a.GetBorderExpansionLevel() + 1 >= aRank) { owner = a; return true; }

			// Law III
			// The city with the lowest rank claim gets the tile.
			if (aRank > bRank) { owner = b; return true; }
			if (aRank < bRank) { owner = a; return true; }

			// Law IV
			// If the ranks are equal, the city with more culture gets the tile.
			if (a.GetCulture() + a.GetCulturePerTurn() < b.GetCulture() + b.GetCulturePerTurn()) { owner = b; return true; }
			if (a.GetCulture() + a.GetCulturePerTurn() > b.GetCulture() + b.GetCulturePerTurn()) { owner = a; return true; }

			// Law V
			// If the cultures are equal the oldest city gets the tile.
			// TODO: track city age - for now we are going to skip this.
			// return a;

			// Law VI
			// Starting North of the disputed tile, we go counter-clockwise
			// trying to find the first tile that has one of the competing cities.
			// We start at (rank - 1) because the rank distance does not necessarily reflect the actual "ring"
			// the city tile is in, so a tile at rank 3, could well mean it's in the 2nd ring.
			for (int r = aRank - 1; r <= aRank; r++) {
				if (r <= 0) continue;
				Tile winner = t.FindInRing(r, tile => tile.HasCity() && (tile.cityAtTile == a || tile.cityAtTile == b), false);
				if (winner == null) continue;
				owner = winner.owningCity;
				return true;
			}

			// should never happen, if it does some part of the algorithm has gone wrong
			throw new Exception($"Failed to resolve ownership of {t} between {a.name} and {b.name}, something went wrong");
		}
	}

	public static class GameDataUtils {
		public static ID GenerateID(this GameData gameData, string identifier) {
			return gameData.ids.CreateID(identifier);
		}
	}
}

