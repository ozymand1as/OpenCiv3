using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine.AI.StrategicAI;
using C7Engine;
using MoonSharp.Interpreter;
using Serilog;
using static C7GameData.EraUtils;
using static C7GameData.MultiTurnDeal;
using static C7GameData.PlayerRelationship;
using static C7GameData.Tile;

namespace C7GameData {

	public struct PlayerCommerceBreakdown {
		public int corrupted;           // Amount of commerce lost directly to corruption
		public int taxes;               // Amount of treasury income from REGULAR citizens working tiles
		public int taxmenTaxes;         // Amount of treasury income from tax collector specialists
		public int beakers;             // Amount of commerce going to science
		public int happiness;           // Amount of commerce going to entertainment
		public int fromOtherCivs;       // Income from other Civ GPT deals
		public int toOtherCivs;         // Expenses paid to other Civ GPT deals
		public int interest;            // Interest income from Wall Street-flag small wonder
		public int maintenance;         // Expenses due to aggregate building maintenance
		public int unitSupport;         // Expenses due to unit support costs
		public int wealthProduction;    // Amount of extra commerce from "building" an Inflow that produces commerce

		public int Inflows() {
			return corrupted + taxes + taxmenTaxes + beakers + happiness + fromOtherCivs + interest + wealthProduction;
		}

		public int Outflows() {
			return corrupted + beakers + happiness + toOtherCivs + maintenance + unitSupport;
		}

		public int Netflows() {
			return Inflows() - Outflows();
		}

		public int CityInflows() {
			return corrupted + taxes + beakers + happiness + wealthProduction;
		}
	}

	public class Alliance {
		public int index;
		public string name;

		public Alliance(int index, string name) {
			this.index = index;
			this.name = name;
		}
	}

	public class Player {
		private static ILogger log = Log.ForContext<Player>();

		public ID id { get; internal set; }
		public int primaryColorIndex;
		public int secondaryColorIndex;
		public bool isBarbarians { get => civilization.isBarbarian; }
		//TODO: Refactor front-end so it sends player GUID with requests.
		//We should allow multiple humans, this is a temporary measure.
		public bool isHuman = false;
		public bool hasPlayedThisTurn = false;
		public bool skipFirstTurn = false;

		// Has this player been defeated?
		public bool defeated = false;

		// The turn on which this player's Golden Age ends. -1 means the player
		// has never had one; Civ3 never writes -1 back, so a civ has at most one
		// Golden Age per game.
		public int goldenAgeEndTurn = -1;

		// The Age of Science is a separate, repeatable 20-turn window during
		// which the civ's research is 25% more productive. Its duration is a
		// literal in Civ3, not RULE.GoldenAgeDuration.
		public bool ageOfScienceActive = false;
		public int ageOfScienceEndTurn = 0;
		public const int AgeOfScienceDuration = 20;
		public const float AgeOfScienceResearchMultiplier = 1.25f;

		// Set once when a unit carried inside one of this civ's armies wins a
		// battle (Civ3's LSF_HAS_VICTORIOUS_ARMY, leader +0x40 bit 0). It is a
		// one-way latch: Civ3 never clears it. The building flag that is expected
		// to read it (Building.requiresVictoriousArmy) is imported but not yet
		// consumed, because the read site is an open item.
		public bool hasVictoriousArmy = false;

		public Civilization civilization;

		// Answers if this player-civ is simply included in the game.
		// Some .biq scenarios contain players/civs in their data
		// that are not a part of the gameplay.
		// ex. Mongols in `4 Middle Ages.biq` scenario.
		public bool isIncludedInGame = true;

		// Answers if the human player can pick and play as this player-civ
		// or is it only an AI player
		public bool canBePicked = true;

		public List<MapUnit> units = new List<MapUnit>();
		public List<City> cities = new List<City>();
		public TileKnowledge tileKnowledge { get; private set; }

		//Ordered list of priority data.  First is most important.
		public List<StrategicPriority> strategicPriorityData = new List<StrategicPriority>();

		// A map from player id to the relationship this player has with the other player.
		public Dictionary<ID, PlayerRelationship> playerRelationships = new();

		// The list of techs known by this player.
		public HashSet<ID> knownTechs = new();

		// A mapping of resource types to their corresponding tiles within player territory.
		// Provides lookup of controlled resources without scanning the entire map.
		public Dictionary<Resource, List<Tile>> resourcesInBorders;

		// The tech the player is currently researching.
		public ID currentlyResearchedTech { get; private set; }

		// A queue of technologies the player has specified they want to research
		public Queue<Tech> ResearchQueue { get; private set; } = new();

		// The civilopedia name of the era this player is in.
		//
		// The civilopedia name is what is used for art lookups, not the actual
		// name.
		public string eraCivilopediaName;

		public int turnsUntilPriorityReevaluation = 0;

		// The values of the science/happiness/tax sliders (tax is implicit)
		// A value of 1 => 10%, a value of 10 => 100%.
		//
		// INVARIANT: LuxuryRate + ScienceRate + TaxRate = 10
		public int luxuryRate = 0;
		public int scienceRate = 5;
		public int taxRate = 5;

		// These values could be added in the rules or something,
		// and also synchronised with the HSlider bar in the editor.
		// For now, I am leaving them hardcoded here.
		public int maxScienceRate { get; private set; } = 10;
		public int minScienceRate { get; private set; } = 0;
		public int maxLuxuryRate { get; private set; } = 10;
		public int minLuxuryRate { get; private set; } = 0;

		// The amount of gold this player has.
		private int _gold = 0;
		public int gold {
			get => _gold;
			set {
				if (value < 0) {
					// TODO: the exception is ok for development, but perhaps a warning log
					// and a Math Max function (0, value) is more appropriate at some point
					throw new Exception($"bad gold value of {value} for {this}");
				}
				_gold = value;
			}
		}

		private int lastGoldPerTurn = 0;

		/// <summary>
		/// Sets the current gold amount of the player. If the add parameter is true, the gold gets appended.<br/>
		/// </summary>
		/// <param name="amount">The number of gold to be added</param>
		/// <param name="add">If true, the gold gets appended, otherwise it overwrites the current value</param>
		[LuaMethod]
		public void SetGold(int amount, bool add = false) {
			if (add)
				this.gold += amount;
			else
				this.gold = amount;
		}

		// The number of "beakers" (gold) spent on the currently researched
		// tech.
		public int beakers = 0;

		// The number of turns the player has been researching the current tech.
		public int turnsResearched = 0;

		// If the government is anarchy (or a govt with the transition bool set
		// to true), the turn number at which switching governments is allowed.
		public int inAnarchyUntilTurn = 0;

		// The current government of the player.
		public Government government;

		// The rules of this game.
		public Rules rules;

		public List<City> citiesWithCorruptionWonders = new();

		// The number of free techs this player has remaining. Free techs can
		// be awarded after researching a tech (like Philosophy) or after
		// completing a wonder (like Theory of Evolution).
		public int freeTechsRemaining = 0;

		// The number of future technologies this player has completed. Once
		// every technology in the tree is known, research continues with the
		// repeatable future technology and each completion increments this
		// (16_science.md §3.1). The score's technology term reads it.
		public int futureTechs = 0;

		public Alliance alliance;

		// How many of each spaceship part this player has built, indexed by the
		// part's slot in VictoryConditions.SpaceshipPartsNeeded.
		public List<int> spaceshipPartsBuilt = new();

		// This player's Conquests victory-point total (spec 25 section 4). Civ3
		// keeps it as a running total at leader +0x11cc and awards to it from the
		// six categories in that section. It is only ever changed while the rules
		// have the victory-point group turned on.
		public int victoryPoints = 0;

		public int EraIndex() {
			return GetEraIndex(eraCivilopediaName);
		}

		public static int EraIndex(string era) {
			if (era == "ERAS_Ancient_Times") {
				return 0;
			} else if (era == "ERAS_Middle_Ages") {
				return 1;
			} else if (era == "ERAS_Industrial_Age") {
				return 2;
			} else if (era == "ERAS_Modern_Era") {
				return 3;
			}
			return -1;
		}

		public static string EraIndexToEra(int index) {
			if (index <= 0) {
				return "ERAS_Ancient_Times";
			} else if (index == 1) {
				return "ERAS_Middle_Ages";
			} else if (index == 2) {
				return "ERAS_Industrial_Age";
			} else {
				return "ERAS_Modern_Era";
			}
		}

		public void AddUnit(MapUnit unit) {
			this.units.Add(unit);
		}

		/// How many armies this player currently owns.
		public int ArmyCount() {
			return units.Count(u => u.IsArmy);
		}

		/// Whether this player currently has an unspent great leader. Civ3 allows
		/// at most one leader unit per civilisation at a time, of either kind.
		public bool HasGreatLeader() {
			return units.Any(u => u.IsGreatLeader);
		}

		/// Whether this player owns any building matching the predicate. Great
		/// Wonders count even when they were built by another civ, as long as we
		/// control them.
		public bool OwnsBuildingWhere(Func<Building, bool> predicate) {
			return cities.Any(c => c.GetBuildings().Any(cb => predicate(cb.building)));
		}

		/// Spawn a great leader of the given kind on the given tile.
		public MapUnit CreateGreatLeader(MapUnit.LeaderKind kind, Tile tile) {
			GameData gameData = EngineStorage.gameData;
			UnitPrototype leaderType = gameData.unitPrototypes.FirstOrDefault(p => p.isLeader);
			if (leaderType == null || tile == null) {
				return null;
			}

			MapUnit leader = leaderType.GetInstance(gameData.GenerateID(leaderType.name), leaderType, this, location: tile);
			leader.leaderKind = kind;
			leader.experienceLevelKey = gameData.defaultExperienceLevelKey;
			leader.experienceLevel = gameData.defaultExperienceLevel;
			leader.hitPointsRemaining = leader.maxHitPoints;

			tile.unitsOnTile.Add(leader);
			gameData.mapUnits.Add(leader);
			AddUnit(leader);

			return leader;
		}

		/// The scientific great leader roll's thresholds, in percent.
		private const int SCIENCE_LEADER_ROLL_BASE = 3;
		private const int SCIENCE_LEADER_ROLL_SCIENTIFIC = 5;

		/// The post-discovery scientific great leader roll. It runs only when a
		/// technology is genuinely researched (never when one is granted silently
		/// by a trade), after the first turn, and only when the rules allow
		/// scientific leaders at all. The one-leader-per-civ cap applies here too.
		public void RollForScientificLeader() {
			if (!rules.AllowScientificLeaders) return;
			if (EngineStorage.gameData.turn <= 0) return;
			if (HasGreatLeader()) return;

			int threshold = civilization.traits.Contains(Civilization.Trait.Scientific)
				? SCIENCE_LEADER_ROLL_SCIENTIFIC
				: SCIENCE_LEADER_ROLL_BASE;
			if (GameData.rng.Next(100) >= threshold) return;

			// Scientific leaders appear at the capital; a civ without one gets
			// nothing.
			City capital = cities.FirstOrDefault(c => c.IsCapital());
			if (capital == null) return;

			CreateGreatLeader(MapUnit.LeaderKind.Scientific, capital.location);
		}

		/// Whether the Age of Science window is currently open. Civ3's test is the
		/// flag plus "current turn <= end turn".
		public bool AgeOfScienceActive => ageOfScienceActive && EngineStorage.gameData.turn <= ageOfScienceEndTurn;

		/// Start the Age of Science. Unlike the Golden Age this can happen more
		/// than once per game, but never while a window is already open.
		public void StartAgeOfScience() {
			if (AgeOfScienceActive) return;
			ageOfScienceActive = true;
			ageOfScienceEndTurn = EngineStorage.gameData.turn + AgeOfScienceDuration;
		}

		/// Whether this player has ever had a Golden Age.
		public bool HasHadGoldenAge => goldenAgeEndTurn != -1;

		/// Whether the Golden Age window is currently open. The window covers
		/// turns [start, goldenAgeEndTurn).
		public bool GoldenAgeActive => goldenAgeEndTurn != -1 && EngineStorage.gameData.turn < goldenAgeEndTurn;

		/// Open this player's Golden Age. The duration comes from
		/// RULE.GoldenAgeDuration, and a Golden Age can never be restarted.
		public void StartGoldenAge() {
			if (goldenAgeEndTurn != -1) return;
			goldenAgeEndTurn = EngineStorage.gameData.turn + rules.GoldenAgeDuration;
		}

		/// The second Golden Age trigger: a civ whose traits are all represented
		/// by Great Wonders it owns opens one. Small wonders do not count, and a
		/// single wonder carrying several of the civ's traits satisfies all of
		/// them. Called whenever a building is added to one of our cities.
		public void CheckGoldenAgeFromWonders() {
			if (goldenAgeEndTurn != -1) return;
			if (civilization == null || civilization.traits.Count == 0) return;

			HashSet<Civilization.Trait> wonderTraits = [];
			foreach (City city in cities) {
				foreach (CityBuilding cb in city.constructed_buildings) {
					if (cb.building.IsGreatWonder()) {
						wonderTraits.UnionWith(cb.building.traits);
					}
				}
			}

			if (civilization.traits.All(wonderTraits.Contains)) {
				StartGoldenAge();
			}
		}

		public void SetCurrentlyResearchedTech(ID id) {
			// Award a free tech if the player has one. A future technology is
			// not a tech in the tree, so it never consumes a free tech - there
			// is nothing left to spend it on once the tree is exhausted.
			if (id != null && freeTechsRemaining > 0 && id != GameData.FutureTechId) {
				--freeTechsRemaining;

				Tech tech = EngineStorage.gameData.techs.Find(x => x.id == id);
				log.Information($"Awarding {tech.Name} to player {this}");
				// A free tech costs nothing, so any beakers carried over from the
				// previously completed tech stay available for the next one.
				CompleteResearchAndBeginNew(EngineStorage.gameData, tech, free: true);
				return;
			}

			currentlyResearchedTech = id;

			// Clear out previous progress.
			beakers = 0;
			turnsResearched = 0;
		}

		public void AddTechItemToResearchQueue(Tech tech) {
			ResearchQueue.Enqueue(tech);
		}

		private IEnumerable<string> CityNameGenerator() {
			List<string> cityNames = civilization.cityNames;
			int loopCounter = 0;

			// Perpetual generator expression to yield all city names lazily
			while (true) {
				foreach (string city in cityNames) {
					if (loopCounter == 0) yield return city;
					else if (loopCounter == 1) yield return $"New {city}";
					else {
						if (loopCounter % 2 == 0) yield return $"{city} {(loopCounter / 2) + 1}";
						else yield return $"New {city} {(loopCounter / 2) + 1}";
					}
				}
				loopCounter++;
			}
		}

		public string GetNextCityName() {
			// Convert to hashset for faster lookups
			HashSet<string> cityNameHashSet = cities.Select(city => city.name).ToHashSet();

			return CityNameGenerator().First(x => !cityNameHashSet.Contains(x));
		}

		public Player() {
			tileKnowledge = new TileKnowledge(this);
		}

		public bool HasExploredTile(Tile tile) {
			return this.tileKnowledge.knownTiles.Contains(tile);
		}

		public bool IsAtPeaceWith(Player other) {
			return AtPeace(this, other);
		}

		public void EnsureRelationshipExists(Player other) {
			if (this.isBarbarians || other.isBarbarians || this.id == other.id || this.defeated || other.defeated)
				return;

			// If the mutual relationship is not established it means that the 2 civs
			// were not aware of each other (aka they just met), and therefore cannot be at war.
			// Initialize the relationship and establish peace automatically between them.
			if (!this.playerRelationships.ContainsKey(other.id) || !other.playerRelationships.ContainsKey(this.id)) {
				this.playerRelationships.TryAdd(other.id, new PlayerRelationship());
				other.playerRelationships.TryAdd(this.id, new PlayerRelationship());
				RegisterMultiTurnDeal(this, other, DEFAULT_PEACE);

				log.Information($"Established first contact and relationship between players {this} and {other}");
			}
		}

		public void DeclareWarOn(Player other, int currentTurn) {
			EnsureRelationshipExists(other);

			// Check to see if there was a sneak attack - we consider a sneak
			// attack any attack where the player's units were inside the
			// borders of the civ they're declaring war on.
			bool isSneakAttack = IsASneakAttackOn(other);

			// TODO: take into account broken right of passage, or other deals, etc?
			// Perhaps we need a dedicated method to calculate this.
			//
			// Refuse contact from the aggressor civ until enough turns have
			// elapsed. The exact civ3 mechanism here is unknown, so we just
			// pick some reasonable random number. To penalize sneak attacks we
			// use a higher upper bound.
			int refuseContactUntilTurn = currentTurn + new Random().Next(5, isSneakAttack ? 16 : 12);

			DeclareWar(this, other, isSneakAttack, refuseContactUntilTurn);

			// Whenever war is declared, re-evaluate priorities.
			turnsUntilPriorityReevaluation = 0;
			other.turnsUntilPriorityReevaluation = 0;
		}

		private bool IsASneakAttackOn(Player other) {
			foreach (Tile location in other.tileKnowledge.knownTiles) {
				if (location.owningCity == null || location.owningCity.owner != other) {
					continue;
				}
				if (location.unitsOnTile.Count == 0) {
					continue;
				}
				if (location.unitsOnTile[0].owner == this) {
					return true;
				}
			}

			return false;
		}

		public bool WillAcceptCommunicationFrom(Player other, int currentTurn) {
			EnsureRelationshipExists(other);

			if (EngineStorage.gameData.AreInLockedWar(this, other)) return false;

			PlayerRelationship pr = playerRelationships[other.id];
			if (AtPeace(this, other)) {
				return true;
			}
			return currentTurn >= pr.refuseContactUntilTurn;
		}

		public bool SitsOutFirstTurn() {
			return this.isBarbarians || this.skipFirstTurn;
		}

		public static bool CanMoveFreely(Player player, Tile sourceTile, Tile targetTile) {
			if (!player.HasExploredTile(targetTile))
				return true;

			Player targetTileOwner = targetTile.OwningPlayer();
			Player sourceTileOwner = sourceTile.OwningPlayer();

			// We are free to move if:
			// - the tile we are on is unowned, or we own the tile
			// - and the tile we are moving to is unowned, or we own the tile
			if ((sourceTileOwner == null || sourceTileOwner == player)
				&& (targetTileOwner == player || targetTileOwner == null))
				return true;

			// All the other cases are either from or to "enemy" tiles
			// and without a RoP agreement the cost is never reduced.
			// check other && RoP
			if (sourceTileOwner != null && sourceTileOwner != player
				&& HaveActiveRightOfPassage(player, sourceTileOwner)) {
				return true;
			}
			if (targetTileOwner != null && targetTileOwner != player
				&& HaveActiveRightOfPassage(player, targetTileOwner)) {
				return true;
			}

			return false;
		}

		public bool KnowsAboutResource(Resource resource) {
			if (resource.Prerequisite == null) {
				return true;
			}
			return knownTechs.Contains(resource.Prerequisite);
		}

		public int RemainingCities() {
			int result = 0;
			foreach (City city in cities) {
				// Destroyed cities have a size of zero.
				if (city.residents.Count > 0) {
					++result;
				}
			}
			return result;
		}

		public override string ToString() {
			if (civilization != null)
				return $"{civilization.name} [{this.id}]";
			return "";
		}

		[LuaMethod]
		public List<Tech> GetKnownTechs() {
			return EngineStorage.gameData.techs.Where(x => this.knownTechs.Contains(x.id)).ToList();
		}

		public PlayerCommerceBreakdown AggregateFlows() {
			var result = new PlayerCommerceBreakdown
			{
				corrupted = 0,
				taxes = 0,
				taxmenTaxes = 0,
				beakers = 0,
				happiness = 0,
				fromOtherCivs = 0,
				toOtherCivs = 0,
				interest = 0,
				maintenance = 0,
				unitSupport = 0,
				wealthProduction = 0
			};

			// If player has no cities, apply no expenses or income.
			// This is how this behaves in regular Civ 3 as well (if you're not defeated, e.g. you still have a King unit or settler)
			if (cities.Count == 0) return result;

			// Assume player has no buildings that generate interest income until we check
			int interestBuildings = 0;

			foreach (City city in cities) {
				CommerceBreakdown cityCommerce = city.CurrentCommerceYield();
				result.corrupted += cityCommerce.corrupted;
				result.taxes += cityCommerce.taxes;
				result.beakers += cityCommerce.beakers;
				result.happiness += cityCommerce.happiness;
				result.maintenance += city.MaintenanceCosts();
				result.wealthProduction += cityCommerce.wealth;

				interestBuildings += city.constructed_buildings.Count(cb => cb.building.treasuryEarnsInterest);

				foreach (CityResident cr in city.residents) {
					// Split city income into "regular citizen" and "tax collector" buckets
					result.taxes -= cr.citizenType.Taxes;
					result.taxmenTaxes += cr.citizenType.Taxes;
				}
			}

			foreach (var pr in playerRelationships.Values) {
				foreach (var mtd in pr.multiTurnDeals) {
					if (mtd.dealSubType == DealSubType.GoldPerTurn) {
						if (mtd.dealDetails == DealDetails.Inbound) result.fromOtherCivs += mtd.goldPerTurn;
						else if (mtd.dealDetails == DealDetails.Outbound) result.toOtherCivs += mtd.goldPerTurn;
					}
				}
			}

			result.unitSupport = TotalUnitsAllowedUnitsAndSupportCost().Item3;

			if (interestBuildings > 0) result.interest = interestBuildings * Math.Min((int)(gold * rules.TreasuryInterestRate), rules.MaxInterest);

			return result;
		}

		public int MaintenanceCosts() {
			int result = 0;
			foreach (City c in cities) {
				result += c.MaintenanceCosts();
			}
			return result;
		}

		// TODO: Add interest and GPT deals
		public int CalculateGoldPerTurn() {
			return AggregateFlows().Netflows();
		}

		public bool WouldAcceptDealFrom(GameData gameData, Player other, TradeOffer theirOffer, TradeOffer ourOffer) {
			// TODO: consider any factors like trade reputations here and culture groups
			// TODO: figure out when peace is acceptable
			int theirGoldValue = theirOffer.GoldEquivalentFor(gameData, this);
			int ourGoldValue = ourOffer.GoldEquivalentFor(gameData, this);
			return theirGoldValue >= ourGoldValue;
		}

		public void ExecuteDeal(GameData gameData, Player other, TradeOffer theirOffer, TradeOffer ourOffer) {
			log.Information($"Executing trade between {this} and {other}");
			log.Information($"  {this} gives {ourOffer.ToString()}, worth {ourOffer.GoldEquivalentFor(gameData, other)} gold");
			log.Information($"  {other} gives {theirOffer.ToString()}, worth {theirOffer.GoldEquivalentFor(gameData, this)} gold)");
			if (theirOffer.partOfPeaceTreaty) {
				SignPeaceAfterWar(this, other, gameData);
			}

			if (ourOffer.gold.HasValue) {
				other.gold += ourOffer.gold.Value;
				this.gold -= ourOffer.gold.Value;
			}
			if (theirOffer.gold.HasValue) {
				this.gold += theirOffer.gold.Value;
				other.gold -= theirOffer.gold.Value;
			}

			other.CompleteResearchAndBeginNew(gameData, ourOffer.techs);
			this.CompleteResearchAndBeginNew(gameData, theirOffer.techs);
		}

		public int EstimateTurnsToResearch(GameData gameData, Tech tech) {
			int beakersPerTurn = 0;
			foreach (City city in cities) {
				beakersPerTurn += city.CurrentCommerceYield().beakers;
			}

			if (beakersPerTurn == 0) {
				// No research is happening.
				return int.MaxValue;
			}

			int remainingCost = gameData.TechCostFor(tech, this);
			int turnsRemaining = (int)Math.Ceiling((double)remainingCost / beakersPerTurn);

			int maxTurnsRemaining = rules.MaximumResearchTime - turnsResearched;
			int minTurnsRemaining = rules.MinimumResearchTime - turnsResearched;

			int result = Math.Min(turnsRemaining, maxTurnsRemaining);
			result = Math.Max(result, minTurnsRemaining);

			return result;
		}

		public string SummarizeScience(GameData gD) {
			Tech tech = gD.GetTech(currentlyResearchedTech);
			if (tech == null) {
				return "Not selected (-- turns)";
			}
			int turns = EstimateTurnsToResearch(gD, tech);
			if (turns == int.MaxValue) {
				return $"{tech.Name} (-- turns)";
			}

			return $"{tech.Name} ({turns} turns)";
		}

		// Get a fresh research queue that overrides any current or previous queues.
		// This queue is being reversed in the end, to provide the actual queue
		// that the player would go through
		public void CalculateFreshTechQueueAndAssignNewCurrent(Tech tech) {
			ResearchQueue.Clear();
			IEnumerable<Tech> techQueue = GetResearchQueueFor(tech, ResearchQueue).Reverse();
			ResearchQueue = new Queue<Tech>(techQueue);

			if (ResearchQueue.Count > 0)
				SetCurrentlyResearchedTech(ResearchQueue.Peek().id);
		}

		// Append a new queue at the tail end of the current queue
		public void CalculateTechQueueAndAppendToCurrentQueue(Tech tech) {
			IEnumerable<Tech> techQueue = GetResearchQueueFor(tech, new Queue<Tech>()).Reverse();
			foreach (Tech t in techQueue) {
				if (!ResearchQueue.Contains(t)) {
					AddTechItemToResearchQueue(t);
				}
			}
		}

		/// <summary>
		/// This produces a queue, but in reverse order, going from the last tech to the first.
		/// We can't reverse when returning from this method, because of its recursive nature,
		/// so this must be done from the caller method.
		/// </summary>
		/// <param name="gameData"></param>
		/// <param name="tech"></param>
		/// <returns></returns>
		private Queue<Tech> GetResearchQueueFor(Tech tech, Queue<Tech> tempQueue) {

			if (tech == null) {
				return new Queue<Tech>();
			}

			List<Tech> requiredTechs = OrderTechs(tech.Prerequisites).ToList();

			// first, add the tech the user clicked
			if (!tempQueue.Contains(tech)) {
				tempQueue.Enqueue(tech);
			}

			// second, get the direct required techs
			foreach (Tech t in requiredTechs) {
				if (!knownTechs.Contains(t.id)) {
					if (!tempQueue.Contains(t)) {
						tempQueue.Enqueue(t);
					}
				}
			}

			// last, recursively get the required techs of these required techs
			foreach (Tech t in requiredTechs) {
				if (!knownTechs.Contains(t.id)) {
					if (t.Prerequisites.Count > 0) {
						GetResearchQueueFor(t, tempQueue);
					}
				}
			}
			return tempQueue;
		}

		/// <summary>
		/// Takes all the techs in the game and keeps only what could be researched next at a particular point in the game.
		/// </summary>
		/// <param name="allTechs"></param>
		/// <returns></returns>
		public HashSet<Tech> GetAvailableTechsToResearch(List<Tech> allTechs) {
			HashSet<Tech> result = new();
			foreach (Tech tech in allTechs) {
				if (knownTechs.Contains(tech.id)) {
					continue;
				}
				if (GetEraIndex(tech.EraCivilopediaName) > EraIndex()) {
					continue;
				}

				bool prereqsKnown = true;
				foreach (Tech prereq in tech.Prerequisites) {
					if (!knownTechs.Contains(prereq.id)) {
						prereqsKnown = false;
						break;
					}
				}
				if (prereqsKnown) {
					result.Add(tech);
				}
			}
			return OrderTechs(result.ToList());
		}

		// Placeholder ordering of techs
		private HashSet<Tech> OrderTechs(List<Tech> techs) {
			if (techs == null || techs.Count == 0) {
				return new HashSet<Tech>();
			}
			// TODO: We would want to eventually order them based on how the AI would do it
			// Details on how Civ3 does it: https://forums.civfanatics.com/threads/what-will-the-ai-research-next.45559/
			return techs.OrderBy(t => t.Cost).ToHashSet();
		}

		public List<Government> GetAvailableGovernments(GameData gameData) {
			List<Government> result = new();
			foreach (Government g in gameData.governments) {
				// You can't deliberately switch into the transition type, you
				// have to switch from one non-transitional type to another.
				if (g.transitionType) {
					continue;
				}

				if (this.HasTech(g.prerequisiteTech)) {

					result.Add(g);
				}
			}
			return result;
		}

		public bool HasTech(ID techId) {
			bool hasTech = techId == null || this.knownTechs.Contains(techId);
			return hasTech;
		}

		// See https://forums.civfanatics.com/threads/everything-about-corruption-c3c-edition.76619/
		//
		// This is the empire-wide information factored out of the rank corruption
		// calculations so it can be reused for anarchy calculations.
		public int GetAdjustedOptimalCityNumber(GameData gameData) {
			int mapOptimalCityNumber = gameData.map.optimalNumberOfCities;
			int percentOptimalCitiesForDifficultyLevel = gameData.gameDifficulty.PercentageOfOptimalCities;

			// TODO: track traits.
			bool isCommercialCiv = false;
			float commercialCivFactor = isCommercialCiv ? .25f : 0;

			// TODO: Handle the SPHQ.
			int numCorruptionReducingSmallWondersInEmpire = 0;
			foreach (City c in cities) {
				// We use constructed_buildings here because great wonders can't
				// supply small wonders like forbidden palaces, so we can avoid
				// doing extra work for each city.
				foreach (CityBuilding cb in c.constructed_buildings) {
					if (cb.building.isForbiddenPalace) {
						++numCorruptionReducingSmallWondersInEmpire;
					}
				}
			}

			float govtFactor = government.corruptionType switch {
				Government.CorruptionType.Minimal => .1f,
				Government.CorruptionType.Nuisance => .1f,
				Government.CorruptionType.Problematic => 0,
				Government.CorruptionType.Rampant => 0,
				Government.CorruptionType.Catastrophic => 0, // anarchy, special cased
				Government.CorruptionType.Communal => 2,
				Government.CorruptionType.Off => 0
			};

			float communalCorruptionFactor =
				government.corruptionType == Government.CorruptionType.Communal ? 3.0f : 3.0f/8.0f;

			float result = mapOptimalCityNumber * percentOptimalCitiesForDifficultyLevel / 100.0f
				  * (1 + commercialCivFactor + govtFactor + communalCorruptionFactor * numCorruptionReducingSmallWondersInEmpire);
			return (int)result;
		}

		// Notes:
		//  - see https://www.civforum.de/showthread.php?3153-Anarchie-Wieviel-Runden-welche-Strategie&p=67682&viewfull=1#post67682 (in German)
		//    This claims Soren Johnson said the time is
		//    1-5 years, random + 0-3 years, depending on the number of cities.
		//    I think the 1-5 is actually 2 to 6, since the min is 2.
		//
		//  - https://forums.civfanatics.com/threads/frequently-asked-questions-civ3-play-the-world-conquests.170282/
		//    For Religious civilizations, anarchy only lasts 1 turn in Vanilla
		//    and Play the World, and 2 turns in Conquests. For non-Religious
		//    civilizations, the formula is: 1 (2 for Conquests) + random number
		//    between 1-4 + number between 0-3 depending on size of your empire.
		public int GetTurnsOfAnarchyForTransition(GameData gameData) {
			if (civilization.traits.Contains(Civilization.Trait.Religious)) {
				return 2;
			}

			// We add Next(3)+Next(3) to roughly approximate a normal
			// distribution. With the base of 2, this gets us a random value
			// between 2 and 6.
			int randomPortion = 2 + GameData.rng.Next(3) + GameData.rng.Next(3);

			// Now we use the OCN to determine the city factor, which is between
			// 0 and 3. This means that sprawling empires will have longer
			// anarchy, but this will scale with map size.
			float numCities = cities.Count;
			int cityPortion = (int)Math.Min(3f, 3f * numCities / GetAdjustedOptimalCityNumber(gameData));

			// The result is the sume of the random portion and the city portion,
			// with the AI cap applied if relevant for this difficulty.
			//
			// Note that this AI transition time is applied after the early
			// return for religious civs. There is a known strategy on higher
			// difficulty levels of using religious civs to help cut down on the
			// AI advantage due to this fact.
			int result = randomPortion + cityPortion;
			if (!isHuman && gameData.gameDifficulty.MaxAiGovernmentTransitionTime > 0) {
				result = Math.Min(result, gameData.gameDifficulty.MaxAiGovernmentTransitionTime);
			}
			return result;
		}

		public void DoPerTurnFinanceUpdates(GameData gameData) {
			if (isBarbarians) {
				return;
			}

			// Process per-city contributions.
			//
			// TODO: consider making this return a tuple too. Or maybe return all
			// the gold accounting stuff in a struct, for one pass over the cities.
			foreach (City city in cities) {
				beakers += city.CurrentCommerceYield().beakers;
			}

			// Ensure we never go below 0 gold.
			while (gold + CalculateGoldPerTurn() < 0) {
				// Start by disbanding units to get things under control.
				var (_, _, unitSupportCost) = TotalUnitsAllowedUnitsAndSupportCost();
				if (unitSupportCost > 0) {
					for (int i = 0; i < unitSupportCost / government.unitCost; ++i) {
						MapUnit unitToRemove = units[GameData.rng.Next(units.Count)];
						log.Information($"{this} is out of gold, disbanding {unitToRemove} at {unitToRemove.location} to being unit support costs under control");
						gameData.RemoveUnit(unitToRemove);
					}
					continue;
				}

				// If we're under the unit support cap, try lowering our science
				// budget.
				if (scienceRate > 0) {
					--scienceRate;
					++taxRate;
					continue;
				}

				// If that wasn't sufficient, go after luxuries.
				if (luxuryRate > 0) {
					--luxuryRate;
					++taxRate;
					continue;
				}

				// If the budget still isn't under control, something is wrong.
				throw new Exception($"{this} was unable to get the budget under control despite being under the unit support cap and zeroing out the sliders (gold={gold}, gpt={CalculateGoldPerTurn()})");
			}

			lastGoldPerTurn = CalculateGoldPerTurn();
			gold += lastGoldPerTurn;
		}

		public void HandleCityUpdates(GameData gameData) {
			foreach (City c in cities) {
				// Ensure borders expand before we assign the new citizen, so that
				// the new citizen can go on one of our new tiles.
				if (c.UpdateCultureAndCheckForExpansion()) {
					gameData.UpdateTileOwners();

					// Update the trade network if borders expanded, as a new
					// resource may be part of the network.
					EngineStorage.gameData.InvalidateCachedTradeNetwork();
				}

				c.HandleCityGrowth(gameData);
				c.HandleCityProduction(gameData);

				// Civ3 rolls for a new polluted tile once per city per turn
				// (18_terrain_improvement.md section 6.3).
				Pollution.SpawnPollution(c);
			}
		}

		public void DoPerTurnScienceUpdates(GameData gameData) {
			if (currentlyResearchedTech == null) {
				return;
			}

			// TODO: This isn't quite accurate. This should only be
			// incremented if the player is actually spending money on
			// research, or has a science specialist.
			turnsResearched++;

			// Check to see if the player has finished researching their
			// tech, and if they have, add it to the list of known techs
			Tech tech = gameData.GetTech(currentlyResearchedTech);
			if (EstimateTurnsToResearch(gameData, tech) > 0) {
				return;
			}

			CompleteResearchAndBeginNew(gameData, tech);

			// A researched technology can produce a great scientific leader. A
			// technology granted by a trade does not, so this is deliberately not
			// inside CompleteResearchingTech.
			RollForScientificLeader();
		}

		private void CompleteResearchAndBeginNew(GameData gameData, IEnumerable<Tech> techs) {
			// Null means no tech was completed, in which case the player's current
			// research progress must be left alone.
			int? overflow = null;
			foreach (Tech tech in techs) {
				overflow = CompleteResearchingTech(gameData, tech);
			}
			PlayerAI.MaybePickTechToResearch(this, gameData);
			CarryOverflowIntoNextTech(overflow);
		}
		private void CompleteResearchAndBeginNew(GameData gameData, Tech tech, bool free = false) {
			int? overflow = CompleteResearchingTech(gameData, tech, free);
			PlayerAI.MaybePickTechToResearch(this, gameData);
			CarryOverflowIntoNextTech(overflow);
		}

		// Beakers left over after a tech is completed are carried into the next
		// technology instead of being discarded. Unlike Civ3, which zeroes the
		// research counter on completion, this is a deliberate improvement, so
		// an overshooting turn is not wasted.
		private void CarryOverflowIntoNextTech(int? overflow) {
			if (overflow == null) {
				return;
			}
			// With no next tech (an exhausted tech tree) and no pending free tech
			// to spend, there is nowhere for the surplus to go.
			if (currentlyResearchedTech == null && freeTechsRemaining == 0) {
				return;
			}
			beakers = overflow.Value;
		}

		private int CompleteResearchingTech(GameData gameData, Tech tech, bool free = false) {
			// Civ3's future technology is repeatable: no tech is recorded, the
			// era is not advanced, and completing one simply increments the
			// player's future-tech counter (16_science.md §3.1). The progress
			// and turn counters are zeroed by SetCurrentlyResearchedTech, as on
			// every other completion.
			if (gameData.IsFutureTech(tech)) {
				++futureTechs;
				SetCurrentlyResearchedTech(null);
				// No surplus: a future-tech completion zeroes the research counter
				// (16_science.md §2.7), so there is no overflow to carry. Returning
				// zero rather than null keeps CarryOverflowIntoNextTech's contract
				// honest - null means "no tech was completed, leave progress alone",
				// and here a tech *was* completed and the progress is deliberately
				// discarded.
				return 0;
			}

			// If this tech awards the first civ to research it a free tech and
			// no other civs know about the tech, this player gets the bonus.
			if (tech.BonusTechToFirstCivThatResearches) {
				bool awardBonus = true;
				foreach (Player p in gameData.players) {
					if (p.knownTechs.Contains(tech.id)) {
						awardBonus = false;
					}
				}
				if (awardBonus) {
					++freeTechsRemaining;
				}
			}

			// Keep the accumulated progress before the setter below clears it.
			int progress = beakers;

			knownTechs.Add(tech.id);
			// trigger callback for techs that enable improvements to redraw map
			TechImprovementCallback(this, tech);

			AwardVictoryPointsForAdvance(gameData, tech);

			// A trade advance can open (or, once the Great Lighthouse is obsolete,
			// close) sea routes, so the cached network is no longer valid.
			if (tech.EnablesTradeOverSea || tech.EnablesTradeOverOcean) {
				gameData.InvalidateCachedTradeNetwork();
			}

			SetCurrentlyResearchedTech(null);

			// The tech's full cost, ignoring progress. Progress is only subtracted
			// while the tech is the current target, and it is no longer the
			// target, so this is the tech's real cost.
			int overflow = free ? progress : Math.Max(0, progress - gameData.TechCostFor(tech, this));

			// remove completed tech from the current research queue
			if (ResearchQueue.Count > 0) {
				ResearchQueue.Dequeue();
			}

			if (CanAdvanceToNextEra(gameData)) {
				eraCivilopediaName = GetNextEraNameByIndex(EraIndex());
			}

			return overflow;
		}

		/// <summary>
		/// Grants a technology outright, without the research bookkeeping. Used
		/// by effects that hand a tech over directly, such as a goody hut.
		/// </summary>
		public void GrantTech(GameData gameData, Tech tech) {
			if (tech == null || knownTechs.Contains(tech.id)) {
				return;
			}

			knownTechs.Add(tech.id);
			TechImprovementCallback(this, tech);

			AwardVictoryPointsForAdvance(gameData, tech);

			if (CanAdvanceToNextEra(gameData)) {
				eraCivilopediaName = GetNextEraNameByIndex(EraIndex());
			}
		}

		private static void TechImprovementCallback(Player player, Tech tech) {
			var terraforms = EngineStorage.gameData.Terraforms;
			if (terraforms.Any(t => t.Improvement is { layer: TerrainImprovement.Layer.Roads } && t.RequiredTech == tech.id)) {
				foreach (var city in player.cities) {
					var cityLoc = city.location;
					TryAddRoad(cityLoc, cityLoc.HasRoad(), cityLoc.HasRailroad());
					TryAddRailroad(cityLoc, cityLoc.HasRailroad());
				}
			}
		}

		private bool CanAdvanceToNextEra(GameData gameData) {
			foreach (Tech t in gameData.techs) {
				if (t.EraCivilopediaName != eraCivilopediaName) {
					continue;
				}

				if (knownTechs.Contains(t.id)) {
					continue;
				}

				// This is a tech in our era that we don't know. If it is
				// required for era advancement we can't go to the next era yet.
				if (t.RequiredForEraAdvancement) {
					return false;
				}
			}

			return true;
		}

		public (int, int, int) TotalUnitsAllowedUnitsAndSupportCostRaw() {
			int freeUnits = 0;

			Difficulty difficulty = EngineStorage.gameData.gameDifficulty;
			if (!isHuman) {
				freeUnits += difficulty.AdditionalFreeUnitSupport;
			}

			foreach (City city in cities) {
				if (!isHuman) {
					freeUnits += difficulty.UnitSupportBonusForEachSettlement;
				}

				if (city.residents.Count <= rules.MaximumLevel1CitySize) {
					freeUnits += government.freeUnitsPerTown;
				} else if (city.residents.Count <= rules.MaximumLevel2CitySize) {
					freeUnits += government.freeUnitsPerCity;
				} else {
					freeUnits += government.freeUnitsPerMetropolis;
				}
			}

			freeUnits += units.Count(u => u.IsCaptive());

			if (government.allUnitsFree) {
				freeUnits = units.Count;
			}

			int totalUnits = units.Count;
			int allowedUnits = freeUnits;
			int unitSupportCost = Math.Max(0, (totalUnits - allowedUnits) * government.unitCost);
			return (totalUnits, allowedUnits, unitSupportCost);
		}

		[MoonSharpHidden]
		public (int, int, int) TotalUnitsAllowedUnitsAndSupportCost() {
			(int totalUnits, int allowedUnits, int unitSupportCost) result = TotalUnitsAllowedUnitsAndSupportCostRaw();

			foreach (City city in cities) {
				// unitSupport lua infow
				if (city.itemBeingProduced is Inflow inflowUnitSupport && inflowUnitSupport.TryGetInflowYieldFunc(InflowYield.unitsupport, out var unitSupportYieldFunc)) {
					int unitSupportLess = unitSupportYieldFunc.Invoke(new ScriptContext(this, city));
					result.unitSupportCost -= unitSupportLess;
				}
			}

			result.unitSupportCost = Math.Max(0, result.unitSupportCost);

			return (result.totalUnits, result.allowedUnits, result.unitSupportCost);
		}

		// See https://forums.civfanatics.com/threads/military-advisor-relative-strength-assessment-definition.62980/post-1211499 and
		// https://forums.civfanatics.com/threads/study-of-inner-workings-of-military-advisor.83599/
		public float CalculateMilitaryStrength() {
			float result = 4; // The +4 base is hypothesized to avoid div by 0 bugs.
			foreach (MapUnit unit in units) {
				UnitPrototype up = unit.unitType;
				result += unit.maxHitPoints * (up.attack * 3 + up.defense * 2) + up.bombard;
			}
			return result;
		}

		public enum MilitaryStrength {
			WeakTo,
			EquivalentTo,
			StrongTo,
		};

		// See https://forums.civfanatics.com/threads/study-of-inner-workings-of-military-advisor.83599/
		public MilitaryStrength CompareMilitaryStrengthTo(Player other) {
			float us = CalculateMilitaryStrength();
			float them = other.CalculateMilitaryStrength();

			if (us < 0.8 * them) {
				return MilitaryStrength.WeakTo;
			} else if (us > 1.2 * them) {
				return MilitaryStrength.StrongTo;
			} else {
				return MilitaryStrength.EquivalentTo;
			}
		}

		// The city that acts as this civ's capital. Civ3 uses the leader's
		// capital city, and falls back to the first city in scenarios that
		// have no palace.
		public City GetCapitalCity() {
			return cities.Find(x => x.IsCapital()) ?? cities[0];
		}

		public void DoCorruptionCalculations(GameData gameData) {
			if (cities.Count == 0) {
				return;
			}

			// Precalculate the cities of interest for distance corruption.
			citiesWithCorruptionWonders.Clear();
			bool foundCapital = false;
			foreach (City c in cities) {
				if (c.IsCapital()) {
					citiesWithCorruptionWonders.Add(c);
					foundCapital = true;
				}
				// We use constructed_buildings here because great wonders can't
				// supply small wonders like forbidden palaces, so we can avoid
				// doing extra work for each city.
				if (c.constructed_buildings.Any(x => x.building.isForbiddenPalace)) {
					citiesWithCorruptionWonders.Add(c);
				}
			}
			if (!foundCapital) {
				// TODO: Ensure we always have a capital, even in scenarios
				// without palaces added.
				citiesWithCorruptionWonders.Add(cities[0]);
			}

			// Order the cities by distance to the capital, using OrderBy to get
			// a stable sort (https://stackoverflow.com/a/148123).
			//
			// TODO: re/specs/14_commerce.md section 2, phase 7 breaks ties between
			// equidistant cities with per-city type and counter values and finally
			// the city id. None of those are tracked, so a stable sort is the
			// current approximation.
			City capital = GetCapitalCity();
			List<City> citiesInRankOrdering = cities.OrderBy(x => x.location.RankDistanceTo(capital.location)).ToList();
			for (int i = 0; i < citiesInRankOrdering.Count; ++i) {
				citiesInRankOrdering[i].rankIndex = i;

				// For each city, calculate its corruption level so this
				// calculation doesn't have to be done on the fly.
				citiesInRankOrdering[i].CalculateCorruption(gameData);
			}
		}

		// Call once at turn advance, decrement penalty turns of unhappiness for drafting / whipping
		public void DecrementCityUnhappinessPenalties(GameData gameData) {
			foreach (City c in cities) {
				c.turnsOfUnhappinessDueToPopRushing -= (c.turnsOfUnhappinessDueToPopRushing > 0) ? 1 : 0;
				c.turnsOfUnhappinessDueToDrafting -= (c.turnsOfUnhappinessDueToDrafting > 0) ? 1 : 0;
			}
		}

		public void RecalculateCitizenMoods(GameData gameData, bool goIntoDisorderIfUnhappy = false) {
			foreach (City c in cities) {
				bool wasInDisorder = c.isInCivilDisorder;
				City.Mood cityMood = c.RecalculateCitizenMoods(gameData);
				bool isInDisorder = cityMood == City.Mood.Unhappy && goIntoDisorderIfUnhappy;
				c.isInCivilDisorder = isInDisorder;

				// A city that was already rioting last turn can lose an
				// improvement this turn (spec 15 §6.2). We only do this on the
				// per-turn call, not on the mood recalcs triggered by a city
				// being added or a specialist being cycled.
				if (wasInDisorder && isInDisorder) {
					c.MaybeLootOnRiot(gameData);
				}
			}
		}

		// Returns a list of specialists that this player can use.
		public List<CitizenType> GetKnownSpecialists(GameData gameData) {
			return gameData.citizenTypes.FindAll(x => {
				return !x.IsDefaultCitizen && this.HasTech(x.PrerequisiteTech);
			});
		}

		// Returns the list of all wonders owned by this player, excluding those
		// that have become obsolete.
		public List<Tuple<City, CityBuilding>> GetActiveWonders() {
			List<Tuple<City, CityBuilding>> result = new();
			foreach (City c in cities) {
				foreach (CityBuilding cb in c.constructed_buildings) {
					if (cb.building.greatWonderProperties == null || cb.building.isGreatWonderObsolete(this)) {
						continue;
					}
					result.Add(new Tuple<City, CityBuilding>(c, cb));
				}
			}
			return result;
		}

		public void MaybeSpawnBonusUnits(GameData gD) {
			// Bonus units only spawn on the first turn, if we have a city and
			// are a non-barbarian AI player.
			if (gD.turn != 1 || cities.Count != 1 || isHuman || isBarbarians) {
				return;
			}

			for (int i = 0; i < gD.gameDifficulty.ExtraStartUnit1; ++i) {
				cities[0].AddUnit(gD.unitPrototypes.Find(x => x.name == gD.rules.StartUnitType1), gD);
			}
			for (int i = 0; i < gD.gameDifficulty.ExtraStartUnit2; ++i) {
				cities[0].AddUnit(gD.unitPrototypes.Find(x => x.name == gD.rules.StartUnitType2), gD);
			}
			for (int i = 0; i < gD.gameDifficulty.NumberOfAIDefensiveStartingUnits; ++i) {
				UnitPrototype unit = (UnitPrototype)cities[0].ListProductionOptions(gD).MaxBy(
					x => {
						if (x is UnitPrototype u) {
							return u.defense;
						}
						return -1;
					}
				);
				cities[0].AddUnit(unit, gD);
			}
			for (int i = 0; i < gD.gameDifficulty.NumberOfAIOffensiveStartingUnits; ++i) {
				UnitPrototype unit = (UnitPrototype)cities[0].ListProductionOptions(gD).MaxBy(
					x => {
						if (x is UnitPrototype u) {
							return u.attack;
						}
						return -1;
					}
				);
				cities[0].AddUnit(unit, gD);
			}
		}

		public void UpdateResourcesInBorders(IEnumerable<Tile> ownedTiles) {
			resourcesInBorders = ownedTiles
								.Where(t => t.Resource != Resource.NONE)
								.GroupBy(t => t.Resource)
								.ToDictionary(g => g.Key, g => g.ToList());
		}

		public bool HasRequiredTechnology(IProducible producible) {
			return producible.requiredTech == null ||
				   knownTechs.Contains(producible.requiredTech.id);
		}

		public bool CanBridgeRoads() {
			ID engineeringTechId = EngineStorage.gameData.techs.FirstOrDefault(tech => tech.EnablesBridges)?.id;
			return knownTechs?.FirstOrDefault(tech => tech == engineeringTechId) != null;
		}

		public int ShieldCost(IProducible producible) {
			if (producible == null) {
				return int.MaxValue;
			}
			// At higher difficulties, AI players get a cost discount.
			Difficulty difficulty = EngineStorage.gameData.gameDifficulty;
			float costFactor = isHuman ? 1.0f : difficulty.AiCostFactor / (float)(difficulty.HumanCostFactor);

			return producible.ShieldCost(civilization.traits, costFactor);
		}

		// ---------------------------------------------------------------- victory points

		/// Awards the victory points for discovering a technology (spec 25 section
		/// 4: `tech_value x AdvancementCost`). Civ3 guards this one on a non-zero
		/// turn number as well as on the victory-point group, so a technology
		/// granted while the game is still being set up awards nothing.
		public void AwardVictoryPointsForAdvance(GameData gameData, Tech tech) {
			if (gameData is null || !gameData.VictoryPointsEnabled || tech is null || gameData.turn <= 0) {
				return;
			}

			AddVictoryPoints(VictoryPoints.ForAdvance(tech, gameData.victoryConditions));
		}

		/// Awards the victory points for destroying another civilization's unit
		/// (spec 25 section 4: `(unit_value / 10) x DefeatingOpposingUnitCost`).
		/// Civ3 credits the killer's player, and only when the killer is a real
		/// opponent - a barbarian killer (player slot 0) and a unit destroyed by
		/// its own side both award nothing.
		public void AwardVictoryPointsForUnitKill(GameData gameData, MapUnit killedUnit) {
			if (gameData is null || !gameData.VictoryPointsEnabled || killedUnit is null || isBarbarians) {
				return;
			}

			if (killedUnit.owner == this) {
				return;
			}

			AddVictoryPoints(VictoryPoints.ForUnitKill(killedUnit.unitType, gameData.victoryConditions));
		}

		/// Awards the victory points for completing a Great Wonder (spec 25
		/// section 4: `wonder_value x WonderCost`). Small wonders and ordinary
		/// buildings award nothing.
		public void AwardVictoryPointsForGreatWonder(GameData gameData, Building wonder) {
			if (gameData is null || !gameData.VictoryPointsEnabled || wonder is null || !wonder.IsGreatWonder()) {
				return;
			}

			AddVictoryPoints(VictoryPoints.ForGreatWonder(wonder, gameData.victoryConditions));
		}

		private void AddVictoryPoints(int amount) {
			if (amount > 0) {
				victoryPoints += amount;
			}
		}

		public void UpdateHistory(GameData gameData) {
			if (!gameData.history.ContainsKey(id.ToString()))
				return;

			if (gameData.gameOver) // Game is already over
				return;

			int n = gameData.history[id.ToString()].Count;
			HistTurnRecord lastTurn = gameData.history[id.ToString()].LastOrDefault();

			// TODO: Make formulas moddable

			// "Power is an amalgam of cities, gold, culture, advances, resources, military strength,
			// nuclear weapons, and wonder."
			// Exact formula is unknown, so we repeat the previous value.
			// TODO: Figure out power formula
			int power = (lastTurn?.Power ?? 100);

			// Score is the _average_ of "turn scores".
			// Here we calculate the cumulative moving average: S[n+1] = S[n] + (x[n+1] - S[N])/(n+1)
			int lastScore = (lastTurn?.Score ?? 0);
			float turnScore = ScoreVictory.ComputeTurnScore(this, gameData);
			int score = (int) Math.Floor(lastScore + (turnScore - lastScore) / (1f * (n+1)));

			// Culture is "the sum of the cultural value of all your cities"
			int totalCulture = cities.Sum(c => c.GetCulture());

			gameData.history[id.ToString()].Add(new HistTurnRecord {
				Date = gameData.timeOptions.GetRawNumber(gameData.turn),
				Power = power,
				Score = score,
				Culture = totalCulture,
				// The histograph's "Victory Points" series is the running total at
				// the end of the turn, the same way Score is the running average
				// (spec 25 section 5.3). Which of the two the save's TurnVP column
				// holds is not established by the spec (its writer is open item
				// 7.3), so this is the reading that makes the series a "Victory
				// Points" curve rather than a per-turn delta.
				VP = victoryPoints
			});
		}
	}

}
