using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

/// Civ3's diplomatic victory: a civilization whose alliance holds a strict
/// majority of the surviving civilizations wins. With n survivors the test is
///
///     alliance_size > n / 2
///
/// using truncating integer division, so two of three survivors (2 > 1) win
/// while two of four (2 > 2) do not. Because conquest is evaluated first, an
/// alliance that contains every survivor is awarded to conquest instead.
public class DiplomaticVictory : IVictory {

	public string Header() => "Diplomatic";

	public VictoryStatus Evaluate(Player player, GameData gameData) {
		List<Player> survivors = VictorySupport.SurvivingPlayers(gameData);

		return new VictoryStatus {
			Player = player,
			SurvivingCivs = survivors.Count,
			AllianceSize = survivors.Count(p => VictorySupport.InSameAlliance(p, player)),
		};
	}

	public bool HasVictory(VictoryStatus status) {
		return status.AllianceSize > status.SurvivingCivs / 2;
	}

	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		yield return [
			"",
			"",
			"Elected as leader",
			"",
			"Alliance votes:",
			$"{status.AllianceSize} of {status.SurvivingCivs}"
		];
	}
}
