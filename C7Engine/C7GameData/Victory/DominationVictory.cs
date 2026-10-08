using System;
using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

/// Civ3's domination victory. A civilization (or alliance) wins when it owns
/// strictly more than DominationTerrain per cent of the map's counted tiles and
/// strictly more than DominationPopulation per cent of the world's population.
///
/// "Counted" tiles are land plus coast, so sea and ocean never count. Both
/// comparisons use truncating integer division, which makes them strict: at 66%
/// of 1000 tiles, 661 owned tiles are required, not 660.
public class DominationVictory : IVictory {
	private readonly int _terrainPercent;
	private readonly int _populationPercent;

	public DominationVictory(int terrainPercent, int populationPercent) {
		_terrainPercent = terrainPercent;
		_populationPercent = populationPercent;
	}

	public string Header() => "Domination";

	public VictoryStatus Evaluate(Player player, GameData gameData) {
		int totalTiles = gameData.map.tiles.Count(t => t.IsCountedForDomination());
		int totalPopulation = gameData.cities.Sum(c => c.residents.Count);

		List<Player> team = VictorySupport.TeamOf(player, gameData);
		int teamTiles = gameData.map.tiles.Count(t => t.IsCountedForDomination() && team.Contains(t.OwningPlayer()));
		int teamPopulation = team.Sum(p => p.cities.Sum(c => c.residents.Count));

		return new VictoryStatus {
			Player = player,
			OwnedTiles = teamTiles,
			TotalTiles = totalTiles,
			OwnedPopulation = teamPopulation,
			TotalPopulation = totalPopulation,
		};
	}

	public bool HasVictory(VictoryStatus status) {
		bool enoughTerrain = status.OwnedTiles > _terrainPercent * status.TotalTiles / 100;
		bool enoughPopulation = status.OwnedPopulation > _populationPercent * status.TotalPopulation / 100;

		return enoughTerrain && enoughPopulation;
	}

	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		yield return RequirementPrint("% of world population:", $"{_populationPercent}%");
		yield return PlayerPrint(
			"Your % of world population:",
			Percent(status.OwnedPopulation, status.TotalPopulation),
			rivalStatuses.OrderByDescending(r => r.OwnedPopulation).FirstOrDefault(),
			r => Percent(r.OwnedPopulation, r.TotalPopulation));
		yield return RequirementPrint("% of world area:", $"{_terrainPercent}%");
		yield return PlayerPrint(
			"Your % of world area:",
			Percent(status.OwnedTiles, status.TotalTiles),
			rivalStatuses.OrderByDescending(r => r.OwnedTiles).FirstOrDefault(),
			r => Percent(r.OwnedTiles, r.TotalTiles));
	}

	public Player ChooseWinner(List<Player> candidates, GameData gameData) {
		// Section 2.2 and 5.4: an alliance wins as a unit and is represented by
		// its human member if it has one, otherwise its first (lowest-index)
		// member. An unallied player is a team of one, so the first satisfying
		// player in scan order wins and no human preference is applied - the
		// human preference belongs to the alliance branch alone.
		List<Player> team = VictorySupport.TeamOf(candidates[0], gameData);
		if (team.Count <= 1) {
			return candidates[0];
		}

		return team.FirstOrDefault(p => p.isHuman) ?? team[0];
	}

	private static string[] RequirementPrint(string label, string value) {
		return ["", "", label, value, "", ""];
	}

	private static string[] PlayerPrint(string label, int own, VictoryStatus topRival, Func<VictoryStatus, int> rivalValue) {
		return [
			"",
			"",
			label,
			$"{own}%",
			topRival?.Player?.civilization?.name ?? "",
			topRival == null ? "" : $"{rivalValue(topRival)}%"
		];
	}

	private static int Percent(int owned, int total) {
		return total == 0 ? 0 : 100 * owned / total;
	}
}
