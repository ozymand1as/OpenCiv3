using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

/// Civ3's space-race victory: every part of the player's spaceship must be
/// complete. The requirement per part comes from the rules and is one of each
/// of the ten shipped parts; a player wins as soon as its own part counts meet
/// every requirement.
public class SpaceRaceVictory : IVictory {
	private readonly List<int> _partsNeeded;

	public SpaceRaceVictory(IEnumerable<int> partsNeeded) {
		_partsNeeded = partsNeeded?.ToList() ?? [];
	}

	public string Header() => "Space Race";

	public VictoryStatus Evaluate(Player player, GameData gameData) {
		int built = 0;
		for (int i = 0; i < _partsNeeded.Count; ++i) {
			int have = i < player.spaceshipPartsBuilt.Count ? player.spaceshipPartsBuilt[i] : 0;
			if (have >= _partsNeeded[i]) {
				++built;
			}
		}

		return new VictoryStatus {
			Player = player,
			SpaceshipPartsBuilt = built,
			SpaceshipPartsRequired = _partsNeeded.Count,
		};
	}

	public bool HasVictory(VictoryStatus status) {
		return status.SpaceshipPartsRequired > 0 && status.SpaceshipPartsBuilt >= status.SpaceshipPartsRequired;
	}

	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		var topRival = rivalStatuses.OrderByDescending(r => r.SpaceshipPartsBuilt).FirstOrDefault();

		yield return [
			"",
			"",
			"Parts built:",
			$"{status.SpaceshipPartsBuilt} / {status.SpaceshipPartsRequired}",
			topRival?.Player?.civilization?.name ?? "",
			topRival == null ? "" : $"{topRival.SpaceshipPartsBuilt} / {topRival.SpaceshipPartsRequired}"
		];
	}
}
