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
		// Section 2.9: the highest running score average, with ties going to the
		// first player in scan order.
		return candidates.OrderByDescending(p => {
			HistTurnRecord lastTurn = gameData.history.TryGetValue(p.id.ToString(), out List<HistTurnRecord> history)
				? history.LastOrDefault()
				: null;
			return lastTurn?.Score ?? 0;
		}).First();
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
