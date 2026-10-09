using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using C7GameData;
using Serilog;

namespace C7Engine {

	public class TurnHandling {
		private static ILogger log = Log.ForContext<TurnHandling>();

		public static void OnBeginTurn() {
			GameData gameData = EngineStorage.gameData;
			log.Information("\n*** Beginning turn " + gameData.turn + " ***");
		}

		public static void OnEndTurn(Player player) {
			GameData gameData = EngineStorage.gameData;

			var busyWorkers = gameData.mapUnits.Where(u => u.owner.id == player.id && u.WorkerJob != null);
			foreach (MapUnit busyWorker in busyWorkers)
				if (busyWorker.WorkerJob != null)
					_ = busyWorker.PerformEndOfTurnAction();
		}

		public static void InitTurnData(Player player = null, bool skipTurn = false) {
			GameData gameData = EngineStorage.gameData;
			if (player == null) {
				foreach (MapUnit mapUnit in gameData.mapUnits)
					mapUnit.OnBeginTurn(skipTurn);
			} else {
				foreach (MapUnit mapUnit in gameData.mapUnits.Where(u => u.owner == player))
					mapUnit.OnBeginTurn(skipTurn);
			}
		}

		// Implements the game loop. This method is called when the game is started and when the player signals that they're done moving.
		public static async Task AdvanceTurn() {
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
			GameData gameData = EngineStorage.gameData;

			// Loop ends with a function return once we reach the UI controller during the movement phase
			while (true) {
				bool firstTurn = GetTurnNumber() == 0;

				// Movement phase
				if (await PlayPlayerTurns(gameData, firstTurn)) {
					stopwatch.Stop();
					log.Debug("Turn time took " + stopwatch.ElapsedMilliseconds + " milliseconds");
					return;
				}

				// Clear all wait queue, so if a player ended the turn without handling all waited units, they are selected
				// at the same place in the order. Confirmed this is what Civ3 does.
				UnitInteractions.ClearWaitQueue();

				BarbarianInteractions.SpawnBarbarians(gameData);

				gameData.turn++;
				foreach (Player player in gameData.players) {
					player.MaybeSpawnBonusUnits(gameData);
					player.DecrementCityUnhappinessPenalties(gameData);
					player.RecalculateCitizenMoods(gameData, goIntoDisorderIfUnhappy: true);
					player.DoCorruptionCalculations(gameData);

					// Do the financial updates before updating the cities, so
					// that a newly produced unit won't put a player over the
					// unit support cap and cause them to lose gold unexpectedly.
					player.DoPerTurnFinanceUpdates(gameData);
					player.DoPerTurnScienceUpdates(gameData);

					// Note that we do growth after calculating citizen moods,
					// to ensure that the player has a chance to deal with the
					// unhappiness of a new citizen during their turn.
					log.Information($"\n*** City growth/production for turn {gameData.turn}, player {player} ***");
					player.HandleCityUpdates(gameData);

					player.UpdateHistory(gameData);
				}

				CheckVictory(gameData);

				// Civ3 runs its pollution-driven global-warming pass once per
				// interturn, after every city has been updated
				// (18_terrain_improvement.md section 7).
				Pollution.DoPerTurnGlobalWarming(gameData);

				// Now that the turn is ending, do all the bookkeeping for the
				// start of the next turn. We don't put the "hasPlayedThisTurn"
				// logic in OnBeginTurn because OnBeginTurn is called when a
				// save game is loaded, and that would erase the saved information
				// about which players have played.
				OnBeginTurn();
				InitTurnData();
				foreach (Player player in gameData.players) {
					player.hasPlayedThisTurn = false;
				}
			}
		}

		/// <summary>
		/// Plays the turns for all the players in the game (including barbarians).
		/// </summary>
		/// <param name="gameData"></param>
		/// <param name="firstTurn"></param>
		/// <returns>true when it is time for the human to take control again</returns>
		private static async Task<bool> PlayPlayerTurns(GameData gameData, bool firstTurn) {
			// Order players: Human -> AI -> Barbarian AI
			var orderedPlayers = gameData.players.OrderByDescending(p => !p.isBarbarians).ThenByDescending(p => p.isHuman).ToList();
			foreach (Player player in orderedPlayers) {
				if (player.hasPlayedThisTurn || player.defeated) {
					continue;
				}

				if (firstTurn && player.SitsOutFirstTurn()) {
					continue;
				}

				// The per-turn diplomatic-memory drift, which Leader_begin_turn
				// @ 0x446840 runs for every real civ before its own turn is played
				// (10_turn_sequence.md section 3 step 4). Barbarians have no memory
				// (the original returns immediately for player index 0).
				PlayerRelationship.DecayReputationMemories(player, gameData);

				// The leader start-of-turn espionage state that expires
				// (24_espionage.md section 8.4).
				EspionageAi.BeginTurn(gameData, player);

				if (player.isBarbarians) {
					await BarbarianAI.PlayTurn(player, gameData);
				} else if (!player.isHuman) {
					await PlayerAI.PlayTurn(player, gameData);
				}

				if (player.id != EngineStorage.uiControllerID) {
					OnEndTurn(player);
					player.hasPlayedThisTurn = true;
				}

				//Human player check. Let the human see what's going on even if they are in observer mode.
				if (player.id == EngineStorage.uiControllerID) {
					new MsgStartTurn().send();
					return true;
				}
			}
			return false;
		}

		///Eventually we'll have a game year or month or whatever, but for now this provides feedback on our progression
		public static int GetTurnNumber() {
			return EngineStorage.gameData.turn;
		}

		internal static void CheckVictory(GameData gameData) {
			if (gameData.gameOver)
				return; // Game is already over

			List<Player> candidates = gameData.players
				.Where(p => !p.isBarbarians && !p.defeated)
				.ToList();

			// Civ3 evaluates the conditions in a fixed order and awards only the
			// first one satisfied by anyone, so this loop is condition-major. See
			// re/specs/25_victory_score.md section 2.0 for the order - which the
			// registration order in SaveGame.ConvertVictoryConditions matches -
			// and section 5.4 for each condition's tie-break.
			foreach (IVictory victory in gameData.victories) {
				List<Player> satisfied = [];
				foreach (Player player in candidates) {
					VictoryStatus status = victory.Evaluate(player, gameData);
					if (victory.HasVictory(status)) {
						satisfied.Add(player);
					}
				}

				if (satisfied.Count > 0) {
					DeclareVictory(victory.ChooseWinner(satisfied, gameData), victory, gameData);
					return;
				}
			}
		}

		private static void DeclareVictory(Player winner, IVictory victory, GameData gameData) {
			Log.Information("Player {Winner} has wone a {Victory} victory!",
				winner, victory.Header());

			gameData.winner = winner;
			gameData.gameOver = true;

			new MsgVictory(winner, victory).send();
		}
	}
}
