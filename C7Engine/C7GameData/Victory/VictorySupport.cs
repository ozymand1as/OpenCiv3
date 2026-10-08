using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine;

/// Helpers shared by the victory conditions that reason about the whole set of
/// civilizations rather than about a single player.
internal static class VictorySupport {

	/// The civilizations Civ3 keeps in its surviving-player bitmask: everyone
	/// who is neither a barbarian nor eliminated.
	public static List<Player> SurvivingPlayers(GameData gameData) {
		return gameData.players.Where(p => !p.isBarbarians && !p.defeated).ToList();
	}

	/// Two civilizations are in the same alliance when they are the same
	/// civilization, or when the rules gave them the same alliance. A player
	/// with no alliance is its own singleton team.
	public static bool InSameAlliance(Player a, Player b) {
		return a == b || (a.alliance != null && a.alliance == b.alliance);
	}

	/// The surviving civilizations that would combine their land and
	/// population for the alliance variants of the domination and cultural
	/// checks.
	public static List<Player> TeamOf(Player player, GameData gameData) {
		return SurvivingPlayers(gameData).Where(p => InSameAlliance(p, player)).ToList();
	}
}
