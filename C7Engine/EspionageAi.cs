using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine {
	// The AI's side of espionage: the per-turn driver at 0x445490 and the
	// mood-weighted mission chooser at 0x445160, from re/specs/24_espionage.md
	// section 8.4.
	//
	// The driver runs once per civ per turn over the civs that civ has contact
	// with, and does three things against one of them, in this order:
	//
	//   * establishes an embassy (mission 0) when it is at peace, its diplomat
	//     agent is available, it has no embassy with the target yet, the target
	//     owns a capital, and its treasury is above its espionage reserve for
	//     that action (see EspionageReserve);
	//   * plants a spy (mission 4) when its spy agent is available, it has no
	//     agent planted in the target and no caught-plant latch against it, the
	//     target owns a capital, and a single draw from the game's random source
	//     hits one value out of a divisor that depends on the state of war;
	//   * picks and runs one spy mission through the chooser below, when it
	//     already has an agent planted in the target.
	//
	// THE TARGETS. The driver walks every civ the actor has met, but only the
	// two spy branches are restricted to the human player: the routine tests the
	// target against the game's local-human mask (0xa526bc, the "local human
	// players" bitmask of 10_turn_sequence.md section 1.1) and skips the civ
	// when the bit is clear, so an AI plants agents and runs spy missions
	// against a human only. The embassy branch runs against any civ; against a
	// human target it first passes one draw out of 64, against a non-human
	// target it does not draw at all.
	public static class EspionageAi {
		// No mission chosen.
		public const int NoMission = -1;

		// The weight table at 0x445448, indexed by the acting civ's mood toward
		// the target (DiplomaticAttitude.MoodForScore's five levels). The weight
		// is the BOUND of one uniform draw, and the chooser only acts when that
		// draw lands in the first ten values, so a LARGER weight means a LOWER
		// chance of acting: the chance is 10 / weight. A Gracious civ (mood 0)
		// therefore acts once in 20000 turns and a Furious one (mood 4) every
		// other turn. The mood model's sign convention (mood 0 = Gracious =
		// friendly, mood 4 = Furious = hostile) is the one DiplomaticAttitude
		// states, and reading the table the other way round would make the AI
		// act most against its friends.
		private static readonly int[] MissionWeightByMood = { 200000, 20000, 2000, 200, 20 };

		// The draw must land in 0..9 to pick a mission; 10..weight-1 means the
		// civ does nothing this turn.
		private const int MissionRollBuckets = 10;

		// The embassy is delayed against a human target by one draw out of 64.
		private const int EmbassyAgainstHumanRoll = 0x40;

		// The plant-spy draw's divisor: 16 at peace, 4 at war (the routine's
		// sign-extended -12 added to 16). More spies are planted against a civ
		// the AI is at war with, not fewer.
		private const int PlantSpyRollAtPeace = 16;
		private const int PlantSpyRollAtWar = 4;

		// The per-civ gold reserve the three branches compare the treasury
		// against, as a multiple of the acting civ's city count. The same
		// reserve appears in the AI's hurry and slider code (13_city_economy.md
		// section 7.5, 21_ai.md section 6.4).
		public const int EmbassyReservePerCity = 16;
		public const int PlantSpyReservePerCity = 32;
		public const int SpyMissionReservePerCity = 64;

		// The caught-plant latch is cleared by one draw out of three per turn,
		// per civ the actor has contact with (the leader start-of-turn routine
		// at 0x560d4x, which runs immediately before it calls this driver).
		private const int LatchClearRoll = 3;

		// ---------------------------------------------------------------------
		// The per-turn hook
		// ---------------------------------------------------------------------

		// The per-turn espionage state that expires, for one civ. The original runs
		// it from the leader start-of-turn routine at 0x560d4x, for every leader,
		// immediately before that routine calls the AI driver for a leader that is
		// not a local human; the engine's per-player turn loop is where the other
		// per-turn per-civ bookkeeping (the reputation drift) already lives.
		public static void BeginTurn(GameData gameData, Player player) {
			if (gameData == null || player == null || player.isBarbarians) {
				return;
			}

			ClearExpiredCaughtPlantLatches(gameData, player);
		}

		// The same routine clears the "stolen plans" bit of the per-civ contact
		// word for every civ the actor has contact with, so the plans a Steal
		// Plans mission reveals last one turn. The engine models that window as
		// PlayerRelationship.stolenPlansUntilTurn instead of a bit, so there is
		// nothing to clear here.
		private static void ClearExpiredCaughtPlantLatches(GameData gameData, Player actor) {
			foreach (Player other in gameData.players) {
				if (!PlayerRelationship.TryGetRelationship(actor, other, out PlayerRelationship relationship)) {
					continue;
				}
				// The routine only draws when the latch is actually set.
				if (relationship.plantSpyAttemptWasCaught && GameData.rng.Next(LatchClearRoll) == 0) {
					relationship.plantSpyAttemptWasCaught = false;
				}
			}
		}

		// ---------------------------------------------------------------------
		// The driver (0x445490)
		// ---------------------------------------------------------------------

		// Runs the driver for one civ over every civ it has contact with. The
		// original calls it from the leader start-of-turn routine only when the
		// leader is not a local human, so the AI's own turn is its hook.
		public static void DoEspionage(GameData gameData, Player actor) {
			if (gameData == null || actor == null) {
				return;
			}
			// A mission can capture a city and so defeat a civ, which must not
			// disturb the walk.
			foreach (Player target in gameData.players.ToList()) {
				if (!PlayerRelationship.TryGetRelationship(actor, target, out PlayerRelationship relationship)) {
					continue;
				}
				DoEspionageAgainst(gameData, actor, target, relationship);
			}
		}

		private static void DoEspionageAgainst(GameData gameData, Player actor, Player target, PlayerRelationship relationship) {
			bool atWar = PlayerRelationship.AtWar(actor, target);

			if (!atWar) {
				// Both diplomat branches are gated on the diplomat agent and the
				// embassy reserve; the routine tests those two before it looks at
				// whether the embassy already exists.
				if (Espionage.DiplomatIsAvailable(gameData, actor)
						&& TreasuryExceedsReserve(actor, EmbassyReservePerCity)) {
					// Establish an embassy when there is none yet.
					if (!relationship.embassyEstablished
							&& TargetHasCapital(target)
							&& Espionage.MissionAllowsAgent(gameData.GetEspionageMission(Espionage.BuildEmbassy), EspionageAgent.Diplomat)) {
						if (!target.isHuman || GameData.rng.Next(EmbassyAgainstHumanRoll) == 0) {
							RunChosenMission(gameData, actor, target, Espionage.BuildEmbassy, EspionageAgent.Diplomat);
						}
					}

					// With an embassy in place the diplomat runs the chooser. Its
					// bucket table only ever picks missions 2, 3, 5, 6, 7 and 8,
					// and the agent-mask test in the chooser refuses all but
					// mission 2 for a diplomat, so this is how the AI steals
					// technology.
					if (target.isHuman && relationship.embassyEstablished
							&& TreasuryExceedsReserve(actor, SpyMissionReservePerCity)) {
						RunChosenMission(gameData, actor, target,
							ChooseMission(gameData, actor, target, EspionageAgent.Diplomat), EspionageAgent.Diplomat);
					}
				}
			}

			// The spy branches only ever run against the human player.
			if (!target.isHuman || !Espionage.SpyIsAvailable(gameData, actor)
					|| !TreasuryExceedsReserve(actor, PlantSpyReservePerCity)) {
				return;
			}

			if (!relationship.agentPlanted
					&& !relationship.plantSpyAttemptWasCaught
					&& TargetHasCapital(target)
					&& Espionage.MissionAllowsAgent(gameData.GetEspionageMission(Espionage.PlantSpy), EspionageAgent.Spy)) {
				int divisor = atWar ? PlantSpyRollAtWar : PlantSpyRollAtPeace;
				if (GameData.rng.Next(divisor) == 0) {
					RunChosenMission(gameData, actor, target, Espionage.PlantSpy, EspionageAgent.Spy);
				}
			}

			// A spy mission needs an agent planted in the target, which the
			// plant-spy attempt above may have just planted.
			if (!relationship.agentPlanted || !TreasuryExceedsReserve(actor, SpyMissionReservePerCity)) {
				return;
			}
			RunChosenMission(gameData, actor, target,
				ChooseMission(gameData, actor, target, EspionageAgent.Spy), EspionageAgent.Spy);
		}

		// The treasury test the three branches share: the acting civ's gold must
		// be ABOVE its reserve, not merely equal to it.
		public static bool TreasuryExceedsReserve(Player actor, int reservePerCity) {
			return actor.gold > EspionageReserve(actor, reservePerCity);
		}

		// The reserve the driver compares the treasury against: the acting civ's
		// city count times the action's multiple, raised by 1000 divided by the
		// number of wonders it owns that carry the small-wonder flag word's bit
		// 3 (mask 8 at the building record's +0xf4), which in the shipped rules
		// is Wall Street alone. The term is added only when the count is
		// positive; the division truncates.
		public static int EspionageReserve(Player actor, int reservePerCity) {
			int reserve = actor.cities.Count * reservePerCity;
			int wonders = CountWondersWithEspionageReserveFlag(actor);
			if (wonders > 0) {
				reserve += 1000 / wonders;
			}
			return reserve;
		}

		private static int CountWondersWithEspionageReserveFlag(Player actor) {
			return actor.cities
				.SelectMany(city => city.GetBuildings())
				.Count(cb => cb.building != null
					&& cb.building.treasuryEarnsInterest
					&& (cb.building.isSmallWonder || cb.building.IsGreatWonder()));
		}

		// The original reads the target's capital city id (leader + 0x2c) and
		// treats -1 as "no capital", which is the case a civ with no city is in.
		public static bool TargetHasCapital(Player target) {
			return TargetCapital(target) != null;
		}

		// The city every AI mission is aimed at: the target's capital. The
		// mission start functions resolve the target city from the capital id
		// themselves rather than from a city argument.
		public static City TargetCapital(Player target) {
			return target?.cities.Find(city => city.IsCapital());
		}

		// ---------------------------------------------------------------------
		// The chooser (0x445160)
		// ---------------------------------------------------------------------

		// Picks one mission to run against the target, or NoMission. The mood is
		// the acting civ's mood toward the target, read through the attitude
		// model's own five levels and its own sign convention.
		public static int ChooseMission(GameData gameData, Player actor, Player target, EspionageAgent agent) {
			DiplomaticMood mood = DiplomaticAttitude.MoodToward(actor, target, gameData, suppressStrengthHalving: false);
			return ChooseMissionForWeight(gameData, actor, target, agent, MissionWeight(mood));
		}

		// The weight for one mood, and 0 for anything outside the five levels.
		// The mood model cannot produce a sixth level, so the 0 is a guard
		// rather than a reachable state; the original's out-of-range arm returns
		// 0 for the same reason.
		public static int MissionWeight(DiplomaticMood mood) {
			int index = (int)mood;
			return index >= 0 && index < MissionWeightByMood.Length ? MissionWeightByMood[index] : 0;
		}

		// The chooser with the mood read separated out, so both halves can be
		// pinned. The weight is halved when the actor is at war with the target
		// or when the actor has already caught spies of the target (the pair's
		// reputation record's caught-spy counter, the field the caught-spy
		// handler at 0x502cc0 increments). A weight of 0 means the civ never
		// tries.
		public static int ChooseMissionForWeight(GameData gameData, Player actor, Player target, EspionageAgent agent, int weight) {
			if (weight <= 0) {
				return NoMission;
			}
			if (PlayerRelationship.AtWar(actor, target) || CaughtSpyCount(actor, target) > 0) {
				weight /= 2;
			}
			if (weight <= 0) {
				return NoMission;
			}

			int roll = GameData.rng.Next(weight);
			if (roll >= MissionRollBuckets) {
				return NoMission;
			}

			int mission = MissionForRoll(roll);
			return MissionIsOffered(gameData, actor, target, agent, mission) ? mission : NoMission;
		}

		// The one draw selects a mission by range. The buckets are two wide for
		// missions 2, 6, 7 and 8 and one wide for missions 3 and 5, so the
		// mission ids are not contiguous and the ranges are not equal.
		private static int MissionForRoll(int roll) {
			switch (roll) {
				case 0:
				case 1:
					return Espionage.StealTechnology;
				case 2:
					return Espionage.StealWorldMap;
				case 3:
					return Espionage.StealPlans;
				case 4:
				case 5:
					return Espionage.InitiatePropaganda;
				case 6:
				case 7:
					return Espionage.SabotageProduction;
				default:
					return Espionage.ExposeEnemySpy;
			}
		}

		// The guards a chooser candidate carries. The agent-mask test and the
		// target's government immunity are the human path's own guards; the
		// other two are the chooser's extra conditions.
		public static bool MissionIsOffered(GameData gameData, Player actor, Player target, EspionageAgent agent, int missionId) {
			EspionageMission mission = gameData?.GetEspionageMission(missionId);
			if (mission == null || !Espionage.MissionAllowsAgent(mission, agent)) {
				return false;
			}
			// The chooser reads the target's immunity field for every mission it
			// can pick, including 7 and 8, which the human path's mission menu
			// does not consult.
			if (Espionage.IsImmune(target, missionId)) {
				return false;
			}

			switch (missionId) {
				case Espionage.StealTechnology:
					// The target must know more technologies than the actor, or
					// there is nothing to steal. The human path expresses the
					// same condition inside the mission instead, by scanning for
					// a technology it can take and reporting that it found none.
					return target.knownTechs.Count > actor.knownTechs.Count;
				case Espionage.StealWorldMap:
					// "Worth stealing": the leader method the chooser calls here
					// (vtable slot 0x5c, at 0x449590) walks every map tile and
					// answers positive when any of them is worth taking. Its
					// primary case is a tile the target knows and the actor does
					// not, which is what this models; it also weighs tiles both
					// sides know against per-civ tile data this engine does not
					// keep. The human path has no equivalent guard: a human may
					// always pay for the mission and may find nothing.
					return TargetMapIsWorthStealing(actor, target);
				case Espionage.ExposeEnemySpy:
					// There must be an agent of the target planted in the actor.
					return target.playerRelationships.TryGetValue(actor.id, out PlayerRelationship rel) && rel.agentPlanted;
				default:
					return true;
			}
		}

		// Whether the target knows a tile the actor does not.
		public static bool TargetMapIsWorthStealing(Player actor, Player target) {
			return target.tileKnowledge.AllKnownTiles().Any(tile => !actor.tileKnowledge.isTileKnown(tile));
		}

		// The actor's caught-spy count for the target: the pair's reputation
		// record's index 4, incremented by the caught-spy handler when the
		// RECORD'S OWNER catches a spy belonging to the indexed civ.
		private static int CaughtSpyCount(Player actor, Player target) {
			return actor.playerRelationships.TryGetValue(target.id, out PlayerRelationship rel)
				? rel.reputation.caughtSpies
				: 0;
		}

		// Runs one mission the driver has already cleared, against the target's
		// capital. A negative mission id is the chooser's "nothing this turn".
		private static void RunChosenMission(GameData gameData, Player actor, Player target, int missionId, EspionageAgent agent) {
			if (missionId == NoMission) {
				return;
			}
			Espionage.RunChosenMission(gameData, actor, target, TargetCapital(target), missionId, agent, EspionageSafetyLevel.Carefully);
		}
	}
}
