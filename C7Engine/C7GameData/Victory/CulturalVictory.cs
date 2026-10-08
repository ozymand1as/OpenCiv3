using System;
using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

/// Civ3's cultural victory. It has two independent triggers:
///
/// * one city accumulating OneCityCultureWin culture ("20k"), or
/// * the whole civilization reaching AllCitiesCultureWin culture scaled by the
///   map size, while holding at least twice as much culture as every rival.
///
/// The civilization requirement grows with the map: Civ3 scales the base value
/// by (width + height) / 200 and rounds it to the nearest thousand, clamped to
/// the range 5000..500000. On the standard 100x100 map the base of 100000 is
/// used unchanged; a 130x130 map requires 130000.
public class CulturalVictory : IVictory {
	private readonly int _oneCityCultureWin;
	private readonly int _allCitiesCultureWin;

	public CulturalVictory(int oneCityCultureWin, int allCitiesCultureWin) {
		_oneCityCultureWin = oneCityCultureWin;
		_allCitiesCultureWin = allCitiesCultureWin;
	}

	public string Header() => "Cultural";

	public VictoryStatus Evaluate(Player player, GameData gameData) {
		List<Player> rivals = VictorySupport.SurvivingPlayers(gameData).Where(p => p != player).ToList();

		int topCityCulture = player.cities.Count == 0 ? 0 : player.cities.Max(c => c.GetCultureFor(player));
		int civilizationCulture = player.cities.Sum(c => c.GetCultureFor(player));
		int topRivalCulture = rivals.Count == 0
			? 0
			: rivals.Max(r => r.cities.Sum(c => c.GetCultureFor(r)));

		return new VictoryStatus {
			Player = player,
			TopCityCulture = topCityCulture,
			CivilizationCulture = civilizationCulture,
			CultureRequirement = AllCitiesCultureRequirement(_allCitiesCultureWin, gameData.map.numTilesWide, gameData.map.numTilesTall),
			TopRivalCulture = topRivalCulture,
		};
	}

	public bool HasVictory(VictoryStatus status) {
		bool oneCityVictory = status.TopCityCulture >= _oneCityCultureWin;

		// "own / 2 < rival" is the failure test, so a tie at exactly twice the
		// rival's culture still wins.
		bool allCitiesVictory = status.CivilizationCulture >= status.CultureRequirement
			&& status.CivilizationCulture / 2 >= status.TopRivalCulture;

		return oneCityVictory || allCitiesVictory;
	}

	public IEnumerable<string[]> GenerateStatusRows(VictoryStatus status, List<VictoryStatus> rivalStatuses) {
		var topRivalByCity = rivalStatuses.OrderByDescending(r => r.TopCityCulture).FirstOrDefault();
		yield return [
			"",
			"",
			"One city:",
			$"{status.TopCityCulture} / {_oneCityCultureWin}",
			topRivalByCity?.Player?.civilization?.name ?? "",
			topRivalByCity == null ? "" : $"{topRivalByCity.TopCityCulture}"
		];

		var topRivalByCiv = rivalStatuses.OrderByDescending(r => r.CivilizationCulture).FirstOrDefault();
		yield return [
			"",
			"",
			"Entire civilization:",
			$"{status.CivilizationCulture} / {status.CultureRequirement}",
			topRivalByCiv?.Player?.civilization?.name ?? "",
			topRivalByCiv == null ? "" : $"{topRivalByCiv.CivilizationCulture}"
		];
	}

	/// The map-scaled civilization culture requirement. Civ3 stores 0 in the
	/// BIQ when the scenario uses the default, in which case the base value
	/// stands in.
	public static int AllCitiesCultureRequirement(int baseRequirement, int mapWidth, int mapHeight) {
		if (baseRequirement <= 0) {
			baseRequirement = 100000;
		}

		long scaled = 1000 * ((499 + (long)(mapWidth + mapHeight) * baseRequirement / 200) / 1000);
		return (int)Math.Clamp(scaled, 5000, 500000);
	}
}
