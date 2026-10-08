using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

public class TimeLimitVictory : IVictory {
	private readonly int _turnLimit;

	public TimeLimitVictory(int turnLimit) {
		_turnLimit = turnLimit;
	}

	public string Header() => "Time Limits";

	public VictoryStatus Evaluate(Player player, GameData gameData) {
		return new VictoryStatus {
			Player = player,
			CurrentTurn = gameData.turn
		};
	}

	public bool HasVictory(VictoryStatus status) {
		return status.CurrentTurn >= _turnLimit;
	}

	public Player ChooseWinner(List<Player> candidates, GameData gameData) {
		// Section 5.4: the highest running score average, unless the rules enable
		// victory points and some player has any - then the highest victory-point
		// total wins, with the score breaking ties between players that share it.
		// A stable sort keeps the first player in scan order for full ties.
		if (gameData.VictoryPointsEnabled && candidates.Any(p => p.victoryPoints >= 1)) {
			return candidates
				.OrderByDescending(p => p.victoryPoints)
				.ThenByDescending(p => ScoreOf(p, gameData))
				.First();
		}

		return candidates.OrderByDescending(p => ScoreOf(p, gameData)).First();
	}

	private static int ScoreOf(Player player, GameData gameData) {
		HistTurnRecord lastTurn = gameData.history.TryGetValue(player.id.ToString(), out List<HistTurnRecord> history)
			? history.LastOrDefault()
			: null;
		return lastTurn?.Score ?? 0;
	}

	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		yield return TurnLimitPrint(status, rivalStatuses);
	}

	private string[] TurnLimitPrint(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		return [
			"Turn in game:",
			$"{_turnLimit}",
			"",
			"",
			"Current turn:",
			$"{status.CurrentTurn}"
		];
	}
}
