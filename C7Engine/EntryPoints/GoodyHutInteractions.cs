using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine.AI;
using C7GameData;

namespace C7Engine {
	/// <summary>
	/// Popping a goody hut.
	///
	/// A unit entering a tile that carries a hut consumes it: the hut bit is
	/// cleared first, so a hut always pops exactly once even if the outcome has
	/// no effect, and then an outcome is chosen from the goody-hut table and
	/// applied to the entering player.
	///
	/// An outcome whose own preconditions fail does not simply fizzle. Civ3
	/// re-runs the whole roll (spec section 4.4) until an outcome passes, so the
	/// effective distribution is the table conditioned on the guards, and this
	/// implementation re-rolls the same way. Sub-rolls are therefore ordinary
	/// here: one pop can consume several draws, and how many depends on game
	/// state. The loop still cannot hang, because the Maps outcome has no
	/// precondition and so is always accepted; MaxRollsPerHut is a safety valve
	/// for a ruleset that would somehow make every outcome fail.
	/// </summary>
	public static class GoodyHutInteractions {
		/// <summary>
		/// The last index of the square-ring enumerator that the Maps outcome
		/// walks. The enumerator starts at the centre (index 0), so indices
		/// 1..76 are the complete Chebyshev radius-3 block (48 tiles) plus 28 of
		/// the 32 ring-4 tiles.
		/// </summary>
		public const int MapsRevealWindow = 76;

		/// <summary>
		/// The turn from which the late-game values apply: money is worth 50
		/// rather than 25, and a hut-founded city gains its population roll.
		/// </summary>
		public const int LateGameTurn = 50;

		/// <summary>
		/// A safety valve on the rejection loop. With the shipped table the
		/// Maps outcome has no precondition and ends the loop, so this bound is
		/// never reached; it only exists so that a ruleset or mod that makes
		/// every outcome unacceptable cannot spin forever.
		/// </summary>
		private const int MaxRollsPerHut = 1000;

		/// <summary>
		/// The table row used for a player: the player's difficulty, plus one
		/// unless the civilization is Expansionist. Civ3 stores the difficulty
		/// per leader; OpenCiv3 stores a single game difficulty, so every
		/// player uses that level.
		///
		/// The shipped conquests.biq defines eight difficulty levels
		/// (Chieftain, Warlord, Regent, Monarch, Emperor, Demigod, Deity, Sid),
		/// so row 8 - Sid without the Expansionist trait - is reachable. Spec
		/// section 4.3 lists only seven levels and omits Demigod.
		/// </summary>
		public static int RowIndexFor(GameData gameData, Player player) {
			int difficulty = gameData.difficulties.IndexOf(gameData.gameDifficulty);
			if (difficulty < 0) {
				difficulty = 0;
			}

			bool expansionist = player.civilization != null
				&& player.civilization.traits.Contains(Civilization.Trait.Expansionist);
			return GoodyHutTable.RowIndex(difficulty, expansionist);
		}

		/// <summary>
		/// Consumes the hut on <paramref name="tile"/> on behalf of
		/// <paramref name="player"/> and returns the outcome that was applied.
		/// A drawn outcome whose preconditions fail is re-rolled; the desert
		/// village is a table result of its own and is not re-rolled.
		/// </summary>
		public static GoodyHutOutcome Consume(GameData gameData, Player player, Tile tile) {
			// The hut is cleared before the outcome is chosen.
			tile.hasGoodyHut = false;

			int row = RowIndexFor(gameData, player);
			bool allowCities = gameData.rules == null || gameData.rules.AllowCitiesFromGoodyHuts;

			for (int attempt = 0; attempt < MaxRollsPerHut; ++attempt) {
				GoodyHutOutcome drawn = GoodyHutTable.Roll(row, GameData.rng, allowCities);
				GoodyHutOutcome applied = Apply(gameData, player, tile, drawn);
				if (applied != GoodyHutOutcome.Nothing) {
					return applied;
				}
				if (drawn == GoodyHutOutcome.Nothing) {
					// The table itself returned the desert village, which is a
					// result rather than a rejected outcome, so there is
					// nothing to re-roll. The message was shown by Apply.
					return applied;
				}
			}

			return NothingMessage(player);
		}

		/// <summary>
		/// Applies one drawn outcome, returning the outcome when it took effect
		/// and Nothing when its preconditions were not met. A caller that wants
		/// Civ3's behaviour must treat Nothing as a rejected draw and roll again
		/// (<see cref="Consume"/> does; it is the only production caller). The
		/// deserted-village outcome itself shows its message here, so a rejected
		/// draw is silent.
		/// </summary>
		public static GoodyHutOutcome Apply(GameData gameData, Player player, Tile tile, GoodyHutOutcome outcome) {
			switch (outcome) {
				case GoodyHutOutcome.City:
					return ApplyCity(gameData, player, tile) ? GoodyHutOutcome.City : GoodyHutOutcome.Nothing;
				case GoodyHutOutcome.Tech:
					return ApplyTech(gameData, player) ? GoodyHutOutcome.Tech : GoodyHutOutcome.Nothing;
				case GoodyHutOutcome.Money:
					return ApplyMoney(gameData, player, tile) ? GoodyHutOutcome.Money : GoodyHutOutcome.Nothing;
				case GoodyHutOutcome.Settlers:
					return ApplySettlers(gameData, player, tile) ? GoodyHutOutcome.Settlers : GoodyHutOutcome.Nothing;
				case GoodyHutOutcome.Maps:
					ApplyMaps(gameData, player, tile);
					return GoodyHutOutcome.Maps;
				case GoodyHutOutcome.Mercenaries:
					return ApplyMercenaries(gameData, player, tile) ? GoodyHutOutcome.Mercenaries : GoodyHutOutcome.Nothing;
				case GoodyHutOutcome.Barbarians:
					return ApplyBarbarians(gameData, player, tile) ? GoodyHutOutcome.Barbarians : GoodyHutOutcome.Nothing;
				default:
					return NothingMessage(player);
			}
		}

		/// <summary>
		/// "An advanced tribe has joined us" - founds a city at the hut tile.
		/// </summary>
		private static bool ApplyCity(GameData gameData, Player player, Tile tile) {
			// The rule toggle suppresses this outcome entirely.
			if (gameData.rules != null && !gameData.rules.AllowCitiesFromGoodyHuts) {
				return false;
			}

			// Only an Expansionist civilization can be handed a city.
			if (player.civilization == null
				|| !player.civilization.traits.Contains(Civilization.Trait.Expansionist)) {
				return false;
			}

			// The civilization must already own a city, and must not be ahead
			// of the per-player share of the world's cities.
			if (player.cities.Count == 0 || !PassesCityCountGuard(gameData, player)) {
				return false;
			}

			// The tile must be a legal city site for the civilization. Civ3
			// also requires the global distance-to-the-nearest-city value to
			// exceed 3; OpenCiv3 keeps no such global, so that guard is not
			// modelled.
			if (!tile.IsAllowCities() || tile.HasCity() || tile.hasBarbarianCamp) {
				return false;
			}

			City city = CityInteractions.BuildCity(tile, player, player.GetNextCityName());

			// From turn 50 on the new city also receives rand_int(4) extra
			// population (0..3).
			int extraPopulation = HutCityPopulationBonus(gameData.turn);
			for (int i = 0; i < extraPopulation; ++i) {
				AddCitizen(gameData, city);
			}

			Notify(player, $"An advanced village has joined us! {city.name} is ours.", happy: true);
			return true;
		}

		/// <summary>
		/// The extra population a hut-founded city receives, and the roll that
		/// decides it: nothing before turn 50, and a uniform draw of four
		/// values (0..3) from turn 50 on.
		///
		/// The turn is compared as OpenCiv3 stores it, which is zero based,
		/// against the spec's 50. Whether the two bases coincide for an imported
		/// save is not statically provable, so the boundary keeps the spec's
		/// literal turn number and is flagged here rather than silently
		/// adjusted.
		/// </summary>
		public static int HutCityPopulationBonus(int turn) {
			return turn >= LateGameTurn ? GameData.rng.Next(4) : 0;
		}

		private static void AddCitizen(GameData gameData, City city) {
			CityResident resident = new() {
				nationality = city.owner.civilization,
				city = city,
				citizenType = gameData.citizenTypes.Find(c => c.IsDefaultCitizen),
			};
			city.AddCitizen(resident);
			CityTileAssignmentAI.AssignNewCitizenToTile(gameData, resident);
		}

		/// <summary>
		/// "The tribe taught us ..." - hands over an advance, Ancient era only.
		/// </summary>
		private static bool ApplyTech(GameData gameData, Player player) {
			// The outcome is only possible while the player is in the Ancient
			// era.
			if (player.EraIndex() != 0) {
				return false;
			}

			// Walk the advances in the ruleset's own order, the way the engine
			// walks its advance array, keeping the ones the player may research
			// (unknown, era not ahead of the player, prerequisites known), the
			// ones the player is not already researching, and the ones whose
			// prerequisite tree is at most four levels deep.
			HashSet<Tech> researchable = player.GetAvailableTechsToResearch(gameData.techs);
			List<Tech> candidates = gameData.techs
				.Where(researchable.Contains)
				.Where(t => player.currentlyResearchedTech == null || t.id != player.currentlyResearchedTech)
				.Where(t => PrerequisiteDepth(t) <= MaxPrerequisiteDepth)
				.ToList();
			if (candidates.Count == 0) {
				return false;
			}

			// Civ3 scores every candidate with a per-leader metric plus a
			// uniform rand_int(100) and keeps the lowest. The metric's meaning
			// is not established (spec section 9.3), so the advance's cost
			// stands in for it; the tiebreak is the same one-draw-per-candidate
			// roll the engine makes.
			Tech granted = null;
			int bestScore = int.MaxValue;
			foreach (Tech candidate in candidates) {
				int score = candidate.Cost + GameData.rng.Next(100);
				if (score < bestScore) {
					bestScore = score;
					granted = candidate;
				}
			}

			player.GrantTech(gameData, granted);
			Notify(player, $"The tribe has taught us {granted.Name}.", happy: true);
			return true;
		}

		/// <summary>
		/// The deepest prerequisite chain an advance may have for the Tech
		/// outcome to hand it over: four levels. Zero means the advance has no
		/// prerequisite at all.
		/// </summary>
		public const int MaxPrerequisiteDepth = 4;

		/// <summary>
		/// The height of an advance's prerequisite tree: zero without
		/// prerequisites, otherwise one more than the tallest prerequisite.
		/// Civ3's helper behind this guard is a pure recursion over the four
		/// prerequisite slots, so it measures exactly this.
		/// </summary>
		public static int PrerequisiteDepth(Tech tech) {
			int deepest = 0;
			foreach (Tech prerequisite in tech.Prerequisites) {
				if (prerequisite == null) {
					continue;
				}
				deepest = Math.Max(deepest, 1 + PrerequisiteDepth(prerequisite));
			}
			return deepest;
		}

		/// <summary>
		/// "We got N gold" - 25 gold before turn 50, 50 from turn 50 on.
		/// </summary>
		private static bool ApplyMoney(GameData gameData, Player player, Tile tile) {
			// The hut tile must carry no resource.
			if (tile.Resource != null && tile.Resource != Resource.NONE) {
				return false;
			}

			// The turn is OpenCiv3's zero-based one; see
			// HutCityPopulationBonus for why the spec's boundary is used
			// literally.
			int amount = gameData.turn >= LateGameTurn ? 50 : 25;
			player.gold += amount;
			Notify(player, $"We got {amount} gold from the village.", happy: true);

			// Civ3 also writes RULE.DefaultMoneyResource onto the tile when the
			// rules define one. That field is not imported, and the shipped
			// rules leave it undefined, so there is nothing to place.
			return true;
		}

		/// <summary>
		/// "A friendly tribe wants to join our government" - one settler.
		/// </summary>
		private static bool ApplySettlers(GameData gameData, Player player, Tile tile) {
			// Civ3 tests the two per-leader AI strategy counters for the Settle
			// strategy, which must both be zero. OpenCiv3 does not keep those
			// Civ3 AI counters, so the closest available proxy is used instead:
			// the civilization must own no settler and be building none.
			if (player.units.Any(u => u.unitType.isSettler)) {
				return false;
			}
			if (player.cities.Any(c => c.itemBeingProduced is UnitPrototype proto && proto.isSettler)) {
				return false;
			}

			if (!PassesCityCountGuard(gameData, player)) {
				return false;
			}

			UnitPrototype settler = gameData.unitPrototypes.FirstOrDefault(u => u.isSettler);
			if (settler == null) {
				return false;
			}

			gameData.SpawnUnit(player, settler, tile);
			Notify(player, $"A friendly tribe wants to join our government. We gained a {settler.name}.", happy: true);
			return true;
		}

		/// <summary>
		/// "The tribe gave us maps of their region" - reveals the hut's
		/// neighbourhood.
		/// </summary>
		private static void ApplyMaps(GameData gameData, Player player, Tile tile) {
			// Civ3 walks the tiles after the centre of the square-ring
			// enumerator (indices 1..76, the Chebyshev radius-3 block plus most
			// of ring 4) and reveals each with probability 3/4. A tile on
			// another continent is only revealed if it is coast, and such a
			// tile is skipped without a roll.
			for (int i = 1; i <= MapsRevealWindow; ++i) {
				Tile t = tile.GetTileAtNeighborIndex(i);
				if (t == null || t == Tile.NONE) {
					continue;
				}
				if (t.continent != tile.continent && !t.IsCoast()) {
					continue;
				}
				if (GameData.rng.Next(4) >= 3) {
					continue;
				}
				player.tileKnowledge.AddTileToKnown(t);
			}

			// The outcome also records globally that the map has been revealed.
			// OpenCiv3 has no consumer for the flag yet, but the original sets
			// it, so the state is kept faithful.
			gameData.mapHasBeenRevealed = true;

			player.tileKnowledge.RecomputeActiveTiles();
			Notify(player, "The friendly tribe gave us maps of their region.", happy: true);
		}

		/// <summary>
		/// "A skilled unit joined us" - one unit of a type the player could
		/// field.
		/// </summary>
		private static bool ApplyMercenaries(GameData gameData, Player player, Tile tile) {
			// Civ3 starts its catalogue walk at a random unit-type index and
			// wraps around. The whole catalogue is offered to the walk, so the
			// start index alone decides which acceptable type is reached first.
			int count = gameData.unitPrototypes.Count;
			if (count == 0) {
				return false;
			}

			int start = GameData.rng.Next(count);
			for (int i = 0; i < count; ++i) {
				UnitPrototype candidate = gameData.unitPrototypes[(start + i) % count];
				if (!IsMercenaryCandidate(gameData, player, tile, candidate)) {
					continue;
				}

				gameData.SpawnUnit(player, candidate, tile);
				Notify(player, $"This friendly village gave us a skilled {candidate.name}.", happy: true);
				return true;
			}

			return false;
		}

		/// <summary>
		/// The mercenary picker accepts a type that is available to the
		/// player's civilization, matches the hut tile's land/water domain,
		/// needs no technology or only one of the player's current era, and
		/// is buildable-or-owned by every living player. Civ3 steps through the
		/// catalogue with a modulus-3 twist rather than one type at a time; that
		/// particular stride is not modelled, but the random starting point is.
		/// </summary>
		private static bool IsMercenaryCandidate(GameData gameData, Player player, Tile tile, UnitPrototype proto) {
			// A type nobody can ever build is not a mercenary. Civ3 checks the
			// "unproducible" flag as part of its buildable-by-everyone test.
			if (proto.unproducible) {
				return false;
			}

			// Civ3 first rejects wheeled unit types. OpenCiv3 does not import
			// the wheeled flag, so that condition is missing.
			if (!proto.producibleBy.Contains(player.civilization)) {
				return false;
			}
			if (tile.IsLand() && !proto.IsLandUnit()) {
				return false;
			}
			if (tile.IsWater() && !proto.IsSeaUnit()) {
				return false;
			}

			if (proto.requiredTech != null) {
				Tech required = gameData.techs.Find(t => t.id == proto.requiredTech.id);
				if (required == null || EraUtils.GetEraIndex(required.EraCivilopediaName) != player.EraIndex()) {
					return false;
				}
			}

			foreach (Player other in gameData.players.Where(p => !p.defeated && !p.isBarbarians)) {
				if (other == player) {
					continue;
				}
				if (proto.producibleBy.Contains(other.civilization)) {
					continue;
				}
				if (other.units.Any(u => u.unitType == proto)) {
					continue;
				}
				return false;
			}

			return true;
		}

		/// <summary>
		/// "We have disturbed an angry tribe" - barbarians around the hut.
		/// </summary>
		private static bool ApplyBarbarians(GameData gameData, Player player, Tile tile) {
			// An Expansionist civilization never gets hostile huts.
			if (player.civilization != null
				&& player.civilization.traits.Contains(Civilization.Trait.Expansionist)) {
				return false;
			}

			// The player must own a city and field at least one military unit.
			// Civ3 also requires the global distance-to-the-nearest-city value
			// to be at least 2 and consults a unit-ability test whose operand is
			// not the entering unit (spec section 9.2); neither is modelled.
			if (player.cities.Count == 0 || !player.units.Any(u => u.IsCombatUnit())) {
				return false;
			}

			Player barbarians = gameData.players.Find(p => p.isBarbarians);
			UnitPrototype basic = gameData.barbarianInfo.basicBarbarian;
			if (barbarians == null || basic == null) {
				return false;
			}

			// Civ3 rotates the eight neighbouring directions by the turn
			// number, skips water and occupied tiles, and gives the first,
			// second and third successful spawn a 3/4, 2/3 and 1/2 chance. A
			// failed chance does not end the walk: the remaining neighbours
			// keep trying at the same odds until three units have appeared.
			int spawned = 0;
			for (int k = 1; k <= 8 && spawned < 3; ++k) {
				TileDirection direction = TileDirectionExtensions.All[(gameData.turn + k) % TileDirectionExtensions.All.Length];
				if (!tile.neighbors.TryGetValue(direction, out Tile neighbor)) {
					continue;
				}
				if (neighbor == null || neighbor == Tile.NONE || neighbor.IsWater()) {
					continue;
				}
				if (neighbor.unitsOnTile.Count > 0) {
					continue;
				}

				bool spawn = spawned switch {
					0 => GameData.rng.Next(4) < 3,
					1 => GameData.rng.Next(3) < 2,
					_ => GameData.rng.Next(2) < 1,
				};
				if (!spawn) {
					continue;
				}

				gameData.SpawnUnit(barbarians, basic, neighbor);
				++spawned;
			}

			// The guards above passed, so the outcome took effect even when
			// every chance roll failed; in that case Civ3 records no message.
			if (spawned > 0) {
				Notify(player, $"We have disturbed an angry tribe. {spawned} barbarians appeared!", happy: false);
			}
			return true;
		}

		/// <summary>
		/// A civilization may not be handed a city once it holds more than its
		/// share of the world's cities: total cities divided by the number of
		/// living civilizations minus one.
		/// </summary>
		private static bool PassesCityCountGuard(GameData gameData, Player player) {
			int livingPlayers = gameData.players.Count(p => !p.defeated && !p.isBarbarians);
			if (livingPlayers <= 1) {
				return false;
			}
			return player.cities.Count <= gameData.cities.Count / (livingPlayers - 1);
		}

		private static GoodyHutOutcome NothingMessage(Player player) {
			Notify(player, "This village is deserted.", happy: true);
			return GoodyHutOutcome.Nothing;
		}

		private static void Notify(Player player, string message, bool happy) {
			if (player.isHuman) {
				new MsgShowMilitaryAdvisorPopup(message, happy).send();
			}
		}
	}
}
