using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

/// Civ3's conquest victory. It is awarded in two situations: when a single
/// civilization is left alive, or when every surviving civilization belongs to
/// the same (non-empty) alliance.
public class ConquestVictory : IVictory {

	public string Header() => "Conquest";

	public VictoryStatus Evaluate(Player player, GameData gameData) {
		List<Player> survivors = VictorySupport.SurvivingPlayers(gameData);

		return new VictoryStatus {
			Player = player,
			SurvivingCivs = survivors.Count,
			AllianceSize = survivors.Count(p => VictorySupport.InSameAlliance(p, player)),
		};
	}

	public bool HasVictory(VictoryStatus status) {
		// One civ left, or every survivor shares one alliance. An unaligned
		// player is its own singleton team, so its alliance size never reaches
		// the survivor count while anyone else stands.
		return status.SurvivingCivs > 0
			&& (status.SurvivingCivs == 1 || status.AllianceSize == status.SurvivingCivs);
	}

	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		yield return [
			"",
			"",
			"Eliminate all rivals",
			"",
			"Rivals still alive:",
			$"{status.SurvivingCivs - 1}"
		];
	}
}
