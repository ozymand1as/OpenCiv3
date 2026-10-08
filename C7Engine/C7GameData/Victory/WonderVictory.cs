using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

/// Civ3's wonder victory (type 7). The predicate does not depend on the player:
/// once every Great Wonder in the rules has been built by someone, every
/// surviving civilization satisfies it and section 5.4 decides which of them
/// wins.
///
/// "Every" is literal, and it is the only condition that works this way. Small
/// Wonders and ordinary buildings are not counted, and an unbuilt Great Wonder
/// keeps the game alive even after it has been made obsolete, because Civ3
/// walks the rules' improvement list rather than the set of buildings some
/// civilization could still finish.
public class WonderVictory : IVictory {

	public string Header() => "Wonder";

	public VictoryStatus Evaluate(Player player, GameData gameData) {
		// The rules' Great Wonders. A wonder counts as built when its name is in
		// the game's completed-wonder set, which the city-production path and
		// both import paths maintain.
		List<Building> greatWonders = gameData.Buildings.Where(b => b.IsGreatWonder()).ToList();

		return new VictoryStatus {
			Player = player,
			Score = ScoreOf(player, gameData),
			GreatWondersBuilt = greatWonders.Count(w => gameData.GreatWondersBuilt.Contains(w.name)),
			GreatWondersTotal = greatWonders.Count,
		};
	}

	public bool HasVictory(VictoryStatus status) {
		// "All Great Wonders built", so one unbuilt Great Wonder is enough to
		// keep the condition (and the game) alive for another turn. A ruleset
		// that defines no Great Wonder at all therefore satisfies the condition
		// vacuously; the shipped rules define 29.
		return status.GreatWondersBuilt >= status.GreatWondersTotal;
	}

	/// Section 5.4: the winner is the civilization with the highest score
	/// average, or the highest victory-point total when the rules turn on
	/// victory-point scoring (the 0x2000 rule bit, VictoryLocations). The
	/// members of an alliance pool the metric before they are compared, and the
	/// winning alliance is represented by its human member if it has one. A tie
	/// on the primary metric is broken by the other one.
	///
	/// Victory points are section 2.8 and are not accumulated in this fork yet,
	/// so every civilization is on zero there. With the flag clear the score is
	/// the primary metric, and with it set the score breaks the all-zero
	/// victory-point tie; both readings therefore select the highest score
	/// average, and a further tie falls through to the first candidate in scan
	/// order.
	public Player ChooseWinner(List<Player> candidates, GameData gameData) {
		bool useVictoryPoints = gameData.victoryConditions?.VictoryLocations ?? false;

		List<Player> best = null;
		float bestPrimary = 0;
		float bestSecondary = 0;

		foreach (List<Player> team in Teams(candidates)) {
			float primary = team.Sum(p => useVictoryPoints ? VictoryPointsOf(p) : ScoreOf(p, gameData));
			float secondary = team.Sum(p => useVictoryPoints ? ScoreOf(p, gameData) : VictoryPointsOf(p));

			// Strict comparisons keep the first team in scan order on a full tie.
			if (best == null || primary > bestPrimary || (primary == bestPrimary && secondary > bestSecondary)) {
				best = team;
				bestPrimary = primary;
				bestSecondary = secondary;
			}
		}

		// The human preference belongs to the alliance rule alone, exactly as it
		// does for domination: an unallied civilization wins on its own metric.
		if (best.Count > 1) {
			return best.FirstOrDefault(p => p.isHuman) ?? best[0];
		}
		return best[0];
	}

	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		var topRival = rivalStatuses.OrderByDescending(r => r.GreatWondersBuilt).FirstOrDefault();

		yield return [
			"",
			"",
			"Total to build:",
			$"{status.GreatWondersTotal}",
			"",
			""
		];

		yield return [
			"",
			"",
			"Wonders built:",
			$"{status.GreatWondersBuilt}",
			topRival?.Player?.civilization?.name ?? "",
			topRival == null ? "" : $"{topRival.GreatWondersBuilt} / {topRival.GreatWondersTotal}"
		];
	}

	/// Groups the candidates into the teams that pool their metric: everyone in
	/// an alliance together, and every unallied civilization on its own. The
	/// team order follows the scan order of its first member.
	private static List<List<Player>> Teams(List<Player> candidates) {
		List<List<Player>> teams = [];
		foreach (Player candidate in candidates) {
			List<Player> team = teams.FirstOrDefault(t => VictorySupport.InSameAlliance(t[0], candidate));
			if (team == null) {
				team = [];
				teams.Add(team);
			}
			team.Add(candidate);
		}
		return teams;
	}

	/// The score Civ3 compares here is the running turn-score average, the same
	/// value the turn limit's tie-break reads.
	private static float ScoreOf(Player player, GameData gameData) {
		HistTurnRecord lastTurn = gameData.history.TryGetValue(player.id.ToString(), out List<HistTurnRecord> history)
			? history.LastOrDefault()
			: null;
		return lastTurn?.Score ?? 0;
	}

	/// TODO (re/specs/25_victory_score.md section 2.8): victory points are a
	/// second, independent score that this fork does not accumulate yet, so
	/// every civilization reads zero here.
	private static float VictoryPointsOf(Player player) {
		return 0;
	}
}
