using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine {
	/// <summary>
	/// Popping a goody hut.
	///
	/// A unit entering a tile that carries a hut consumes it: the hut bit is
	/// cleared first, so a hut always pops exactly once even if the outcome has
	/// no effect, and then one outcome is chosen from the goody-hut table and
	/// applied to the entering player.
	///
	/// Civ3 re-runs the roll when an outcome's own preconditions fail. This
	/// implementation does not: a failed guard resolves to Nothing instead. The
	/// reason is that the re-roll is unbounded and would live-lock for a
	/// ruleset that lacks one of the prerequisites (no settler or mercenary
	/// unit type, no barbarian player) while making the number of RNG draws per
	/// hut depend on game state.
	/// </summary>
	public static class GoodyHutInteractions {
		/// <summary>
		/// The table row used for a player: the player's difficulty, plus one
		/// unless the civilization is Expansionist. Civ3 stores the difficulty
		/// per leader; OpenCiv3 stores a single game difficulty, so every
		/// player uses that level.
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
		/// </summary>
		public static GoodyHutOutcome Consume(GameData gameData, Player player, Tile tile) {
			// The hut is cleared before the outcome is chosen.
			tile.hasGoodyHut = false;

			GoodyHutOutcome outcome = GoodyHutTable.Roll(
				RowIndexFor(gameData, player),
				GameData.rng,
				gameData.rules == null || gameData.rules.AllowCitiesFromGoodyHuts);

			return Apply(gameData, player, tile, outcome);
		}

		/// <summary>
		/// Applies a chosen outcome, returning the outcome that actually took
		/// effect. An outcome whose preconditions are not met resolves to
		/// Nothing.
		/// </summary>
		public static GoodyHutOutcome Apply(GameData gameData, Player player, Tile tile, GoodyHutOutcome outcome) {
			switch (outcome) {
				case GoodyHutOutcome.City:
					return ApplyCity(gameData, player, tile) ? GoodyHutOutcome.City : NothingMessage(player);
				case GoodyHutOutcome.Tech:
					return ApplyTech(gameData, player) ? GoodyHutOutcome.Tech : NothingMessage(player);
				case GoodyHutOutcome.Money:
					return ApplyMoney(gameData, player, tile) ? GoodyHutOutcome.Money : NothingMessage(player);
				case GoodyHutOutcome.Settlers:
					return ApplySettlers(gameData, player, tile) ? GoodyHutOutcome.Settlers : NothingMessage(player);
				case GoodyHutOutcome.Maps:
					ApplyMaps(player, tile);
					return GoodyHutOutcome.Maps;
				case GoodyHutOutcome.Mercenaries:
					return ApplyMercenaries(gameData, player, tile) ? GoodyHutOutcome.Mercenaries : NothingMessage(player);
				case GoodyHutOutcome.Barbarians:
					return ApplyBarbarians(gameData, player, tile) ? GoodyHutOutcome.Barbarians : NothingMessage(player);
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

			// The tile must be a legal city site for the civilization.
			if (!tile.IsAllowCities() || tile.HasCity() || tile.hasBarbarianCamp) {
				return false;
			}

			City city = CityInteractions.BuildCity(tile, player, player.GetNextCityName());
			Notify(player, $"An advanced village has joined us! {city.name} is ours.", happy: true);

			// Civ3 also adds rand_int(4) extra population when the turn number
			// is 50 or more. That roll is deliberately omitted: the outcome
			// roll is the only randomness a hut pop may consume.
			return true;
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

			List<Tech> candidates = player.GetAvailableTechsToResearch(gameData.techs)
				.Where(t => player.currentlyResearchedTech == null || t.id != player.currentlyResearchedTech)
				.ToList();
			if (candidates.Count == 0) {
				return false;
			}

			// Civ3 scores candidates with a leader metric plus a rand_int(100)
			// tiebreak. OpenCiv3 has no equivalent leader metric, so the
			// cheapest available advance is chosen, and no tiebreak roll is
			// made.
			Tech granted = candidates.OrderBy(t => t.Cost).First();
			player.GrantTech(gameData, granted);
			Notify(player, $"The tribe has taught us {granted.Name}.", happy: true);
			return true;
		}

		/// <summary>
		/// "We got N gold" - 25 gold before turn 50, 50 from turn 50 on.
		/// </summary>
		private static bool ApplyMoney(GameData gameData, Player player, Tile tile) {
			// The hut tile must carry no resource.
			if (tile.Resource != null && tile.Resource != Resource.NONE) {
				return false;
			}

			int amount = gameData.turn <= 49 ? 25 : 50;
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
			// The civilization must own no settler and be building none.
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
		private static void ApplyMaps(Player player, Tile tile) {
			// Civ3 walks the tiles after the centre of a square-ring
			// enumerator, which covers the complete Chebyshev radius-3 block
			// plus most of ring 4, and reveals each with probability 3/4. The
			// per-tile roll is omitted here to keep a hut pop to a single RNG
			// draw, so the whole block is revealed. A tile on another
			// continent is only revealed if it is coast.
			for (int i = 1; i <= 76; ++i) {
				Tile t = tile.GetTileAtNeighborIndex(i);
				if (t == null || t == Tile.NONE) {
					continue;
				}
				if (t.continent != tile.continent && !t.IsCoast()) {
					continue;
				}
				player.tileKnowledge.AddTileToKnown(t);
			}

			player.tileKnowledge.RecomputeActiveTiles();
			Notify(player, "The friendly tribe gave us maps of their region.", happy: true);
		}

		/// <summary>
		/// "A skilled unit joined us" - one unit of a type the player could
		/// field.
		/// </summary>
		private static bool ApplyMercenaries(GameData gameData, Player player, Tile tile) {
			UnitPrototype chosen = gameData.unitPrototypes.FirstOrDefault(p => IsMercenaryCandidate(gameData, player, tile, p));
			if (chosen == null) {
				return false;
			}

			gameData.SpawnUnit(player, chosen, tile);
			Notify(player, $"This friendly village gave us a skilled {chosen.name}.", happy: true);
			return true;
		}

		/// <summary>
		/// The mercenary picker accepts a type that is available to the
		/// player's civilization, matches the hut tile's land/water domain,
		/// needs no technology or only one of the player's current era, and
		/// is buildable-or-owned by every living player. Civ3 starts the walk
		/// at a random catalogue index; a deterministic walk is used instead
		/// so that the outcome roll stays the only RNG draw.
		/// </summary>
		private static bool IsMercenaryCandidate(GameData gameData, Player player, Tile tile, UnitPrototype proto) {
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
			// second and third spawn a 3/4, 2/3 and 1/2 chance, capped at
			// three units. The chances are omitted so that the outcome roll
			// stays the only RNG draw, so the cap of three is what remains.
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

				gameData.SpawnUnit(barbarians, basic, neighbor);
				++spawned;
			}

			if (spawned == 0) {
				return false;
			}

			Notify(player, $"We have disturbed an angry tribe. {spawned} barbarians appeared!", happy: false);
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
