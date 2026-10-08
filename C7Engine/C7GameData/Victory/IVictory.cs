using System.Collections.Generic;

namespace C7GameData;

public interface IVictory {
	public string Header();
	public VictoryStatus Evaluate(Player player, GameData gameData);
	public bool HasVictory(VictoryStatus status);
	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses);

	/// Chooses the winner when more than one player satisfies this condition in
	/// the same turn. Civ3 gives each condition its own tie-break; the default
	/// is the first candidate in scan order, which is what the conditions
	/// without a documented tie-break do.
	public Player ChooseWinner(List<Player> candidates, GameData gameData) {
		return candidates[0];
	}
}
