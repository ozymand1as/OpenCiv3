using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

/// Civ3's victory-point victory (type 8). It is the first condition in the
/// fixed evaluation order of spec 25 section 2.0 - before conquest - and it is
/// gated by the rules' victory-point group (0x26000) rather than by a condition
/// flag of its own.
///
/// Everyone's total is the running victory-point total; an alliance combines
/// its members' totals. The test is inclusive, unlike domination's: reaching
/// the limit exactly is a win.
public class VictoryPointVictory : IVictory {
	private readonly int _limit;

	public VictoryPointVictory(int limit) {
		_limit = limit;
	}

	public string Header() => "Victory Points";

	public VictoryStatus Evaluate(Player player, GameData gameData) {
		// Section 2.8: alliance games sum the members' totals; an unallied
		// player is a team of one.
		List<Player> team = VictorySupport.TeamOf(player, gameData);

		return new VictoryStatus {
			Player = player,
			VictoryPoints = team.Sum(p => p.victoryPoints),
			VictoryPointLimit = _limit,
		};
	}

	public bool HasVictory(VictoryStatus status) {
		return status.VictoryPointLimit <= status.VictoryPoints;
	}

	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		yield return ["", "", "Limit", $"{_limit}", "", ""];
		yield return [
			"",
			"",
			"Current victory points:",
			$"{status.VictoryPoints}",
			rivalStatuses.OrderByDescending(r => r.VictoryPoints).FirstOrDefault()?.Player?.civilization?.name ?? "",
			$"{rivalStatuses.OrderByDescending(r => r.VictoryPoints).FirstOrDefault()?.VictoryPoints ?? 0}"
		];
	}
}
