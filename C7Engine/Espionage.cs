using System;
using System.Collections.Generic;
using System.Linq;
using C7GameData;

namespace C7Engine {
	// Which of the two abstract per-civ agents is running a mission. Civ3 has
	// no diplomat or spy units; the agents are state gated by a technology and
	// a wonder.
	public enum EspionageAgent {
		Diplomat,
		Spy,
	}

	// The posture a player chooses for the five missions that trade gold for
	// odds.
	public enum EspionageSafetyLevel {
		Immediately = 0,
		Carefully = 1,
		Safely = 2,
	}

	// The outcome of one mission attempt. `ran` is false when the mission never
	// started (unknown mission, unavailable, invalid city, not enough gold, or
	// an effect the engine does not implement yet), in which case no gold was
	// spent.
	public class EspionageMissionResult {
		public bool ran;
		public bool succeeded;
		public bool agentCaught;
		public bool harmlessFailure;
		public int costPaid;
		public int successChance;
		public Tech stolenTech;
		public string message;
	}

	// The espionage subsystem, ported from the Civ3 Conquests engine. The rules
	// follow re/specs/24_espionage.md: the nine-mission table, the cost formula,
	// the success-probability formula and the per-mission resolutions.
	public static class Espionage {
		public const int BuildEmbassy = 0;
		public const int InvestigateCity = 1;
		public const int StealTechnology = 2;
		public const int StealWorldMap = 3;
		public const int PlantSpy = 4;
		public const int StealPlans = 5;
		public const int InitiatePropaganda = 6;
		public const int SabotageProduction = 7;
		public const int ExposeEnemySpy = 8;

		// Indexed by the acting government's DiplomatsAre / SpiesAre field.
		private static readonly int[] ExperienceBonus = { -30, 0, 10, 25 };

		// Indexed by the chosen safety level.
		private static readonly int[] PostureCostPercent = { 100, 150, 200 };
		private static readonly int[] PostureOddsModifier = { -20, 0, 10 };

		// The chance that the second, mission-specific roll passes (spec 5.2).
		private const int StealTechnologySecondRoll = 80;
		private const int StealWorldMapSecondRoll = 80;
		private const int StealPlansExposureRoll = 10;
		private const int SabotageExposureRoll = 20;
		private const int ExposeSpyExposureRoll = 10;

		// ---------------------------------------------------------------------
		// Who may spy (spec 3.1, 3.2)
		// ---------------------------------------------------------------------

		// A civ may use diplomats when it has a capital, knows a technology
		// whose advance flag bit 0 is set, and is not in the government that
		// blocks espionage.
		public static bool DiplomatIsAvailable(GameData gameData, Player player) {
			if (gameData == null || player == null) {
				return false;
			}
			return HasCapital(player)
				&& player.knownTechs.Any(id => gameData.GetTech(id)?.EnablesDiplomats == true)
				&& !BlocksEspionage(player);
		}

		// A civ may use spies when it owns a wonder carrying the "allows spy
		// missions" flag and is not in the government that blocks espionage.
		public static bool SpyIsAvailable(GameData gameData, Player player) {
			if (player == null) {
				return false;
			}
			return player.cities.Any(city => city.GetBuildings().Any(cb => cb.building.allowsSpyMissions))
				&& !BlocksEspionage(player);
		}

		private static bool HasCapital(Player player) {
			return player.cities.Any(city => city.IsCapital());
		}

		// The original engine compares the current government against one
		// special government id (0x9c3dd0). In the shipped rules the only
		// government with TransitionType set is Anarchy, which is the natural
		// reading of that id; Despotism, the DefaultType, does not block
		// espionage.
		private static bool BlocksEspionage(Player player) {
			return player.government == null || player.government.transitionType;
		}

		// A mission's ESPN flags are a mask of which agent kinds may run it.
		public static bool MissionAllowsAgent(EspionageMission mission, EspionageAgent agent) {
			if (mission == null) {
				return false;
			}
			return agent == EspionageAgent.Diplomat ? mission.diplomatAllowed : mission.spyAllowed;
		}

		// Whether the mission menu should offer `missionId` against `target`
		// (spec 3.3). Missions 0 and 4 are self-scoped entries in the original
		// UI; here they are offered against a real target and the per-civ
		// prerequisites are checked instead.
		public static bool MissionIsAvailable(GameData gameData, Player actor, Player target, int missionId, EspionageAgent agent) {
			EspionageMission mission = gameData?.GetEspionageMission(missionId);
			if (mission == null || actor == null || target == null || actor == target) {
				return false;
			}
			if (!MissionAllowsAgent(mission, agent)) {
				return false;
			}
			if (!TryGetRelationship(actor, target, out PlayerRelationship relationship)) {
				return false;
			}
			if (IsImmune(target, missionId)) {
				return false;
			}

			switch (missionId) {
				case BuildEmbassy:
					return !relationship.embassyEstablished && PlayerRelationship.AtPeace(actor, target);
				case PlantSpy:
					return !relationship.agentPlanted && !relationship.plantSpyAttemptWasCaught
						&& PlayerRelationship.AtPeace(actor, target);
				default:
					if (agent == EspionageAgent.Diplomat) {
						// The diplomat path of missions 1 and 2 needs an
						// embassy with the target.
						return relationship.embassyEstablished && PlayerRelationship.AtPeace(actor, target);
					}
					// Every other spy mission needs an agent already planted
					// in the target.
					return relationship.agentPlanted;
			}
		}

		// The target's government is immune to one mission id (spec 2.4). The
		// original engine only consults the field for missions 0 through 6.
		private static bool IsImmune(Player target, int missionId) {
			if (missionId < BuildEmbassy || missionId > InitiatePropaganda) {
				return false;
			}
			return target.government != null && target.government.immuneTo == missionId;
		}

		// ---------------------------------------------------------------------
		// Cost (spec 4.1, 4.2, 4.3)
		// ---------------------------------------------------------------------

		// The per-mission base cost, before the shared scaling term and the
		// safety-level multiplier.
		public static int MissionBaseCost(GameData gameData, Player actor, Player target, City targetCity, int missionId) {
			EspionageMission mission = gameData?.GetEspionageMission(missionId);
			if (mission == null || actor == null || target == null || targetCity == null) {
				return 0;
			}

			int baseCost = mission.baseCost;
			int population = targetCity.residents.Count;

			switch (missionId) {
				case BuildEmbassy:
					return baseCost + population;
				case InvestigateCity:
					return baseCost * population;
				case StealTechnology:
					return target.knownTechs.Count * gameData.map.techRate * baseCost / 100;
				case StealWorldMap:
					return (gameData.map.numTilesWide + gameData.map.numTilesTall) / 2 * baseCost;
				case PlantSpy:
					return baseCost;
				case StealPlans:
					return target.units.Count * baseCost;
				case InitiatePropaganda: {
						int b = baseCost * population;
						int selfCulture = PlayerCulture(actor);
						int targetCulture = PlayerCulture(target);
						int selfGold = actor.gold;
						int targetGold = target.gold;
						return b
							+ (selfCulture > 0 ? b * targetCulture / selfCulture : 0)
							+ (selfGold > 0 ? targetGold * (b / 2) / selfGold : 0);
					}
				case SabotageProduction:
					return targetCity.shieldsStored * baseCost;
				case ExposeEnemySpy:
					return baseCost;
				default:
					return baseCost;
			}
		}

		// The full gold price: the per-mission base, the shared scaling term
		// and the safety-level multiplier. Integer divisions truncate.
		public static int MissionCost(GameData gameData, Player actor, Player target, City targetCity, int missionId, EspionageSafetyLevel safety) {
			int baseCost = MissionBaseCost(gameData, actor, target, targetCity, missionId);
			int scaled = ScaleCost(gameData, actor, targetCity, baseCost);
			int multiplier = MissionOffersSafetyLevel(missionId) ? PostureCostPercent[(int)safety] : 100;
			return scaled * multiplier / 100;
		}

		// The shared scaling term (spec 4.2): a city with more of the actor's
		// nationals in it is cheaper to act against, and distance to the
		// actor's nearest city raises the price.
		private static int ScaleCost(GameData gameData, Player actor, City targetCity, int baseCost) {
			int population = targetCity.residents.Count;
			Rules rules = gameData.rules;
			int townThreshold = rules?.MaximumLevel1CitySize ?? 6;
			int cityThreshold = rules?.MaximumLevel2CitySize ?? 12;

			int tier = population <= townThreshold ? 0
				: population <= cityThreshold ? 1 : 2;
			int ownNationals = targetCity.residents.Count(r => r.nationality == actor.civilization);
			int distance = NearestCityDistance(actor, targetCity);

			return (2 * population - ownNationals + 1) * (baseCost + (tier + 1) * distance) / (2 * population + 1);
		}

		private static int NearestCityDistance(Player actor, City targetCity) {
			if (actor.cities.Count == 0 || targetCity.location == null) {
				return 0;
			}
			int best = int.MaxValue;
			foreach (City city in actor.cities) {
				best = Math.Min(best, city.location.DistanceTo(targetCity.location));
			}
			return best == int.MaxValue ? 0 : best;
		}

		// The five missions that offer a safety level (spec 4.3).
		public static bool MissionOffersSafetyLevel(int missionId) {
			return missionId == StealTechnology || missionId == StealWorldMap
				|| missionId == StealPlans || missionId == SabotageProduction
				|| missionId == ExposeEnemySpy;
		}

		// ---------------------------------------------------------------------
		// Odds (spec 5.1, 6.6)
		// ---------------------------------------------------------------------

		// The percentage the mission's first roll is compared against. The
		// first roll always succeeds for missions 0 and 1 and is impossible for
		// mission 6, which uses per-citizen rolls instead.
		public static int SuccessChance(GameData gameData, Player actor, Player target, int missionId, EspionageAgent agent, EspionageSafetyLevel safety) {
			int starting;
			switch (missionId) {
				case BuildEmbassy:
				case InvestigateCity:
					starting = 100;
					break;
				case PlantSpy:
					starting = 50;
					break;
				case InitiatePropaganda:
					starting = PropagandaChance(gameData, actor, target);
					break;
				default:
					starting = 60 + PostureOddsModifier[(int)safety] + (agent == EspionageAgent.Spy ? 15 : 0);
					break;
			}

			return Math.Clamp(starting + ExperienceBonusFor(actor, agent), 0, 100);
		}

		// The acting government's experience bonus (spec 3.6). There is no
		// per-unit spy veterancy; the government is what makes a spy veteran.
		private static int ExperienceBonusFor(Player actor, EspionageAgent agent) {
			Government government = actor.government;
			int index = government == null ? 1
				: agent == EspionageAgent.Diplomat ? government.diplomatsAre : government.spiesAre;
			return ExperienceBonus[Math.Clamp(index, 0, ExperienceBonus.Length - 1)];
		}

		// The per-citizen propaganda chance, selected from the culture levels by
		// the actor's culture ratio against the target (spec 6.6).
		public static int PropagandaChance(GameData gameData, Player actor, Player target) {
			List<CultureLevel> levels = gameData?.cultureLevels;
			if (levels == null || levels.Count == 0) {
				return 0;
			}

			int targetCulture = PlayerCulture(target);
			if (targetCulture <= 0) {
				return levels[0].chanceOfSuccessfulPropaganda;
			}

			int ratio = 100 * PlayerCulture(actor) / targetCulture;
			foreach (CultureLevel level in levels) {
				if (ratio >= level.cultureRatioPercentage) {
					return level.chanceOfSuccessfulPropaganda;
				}
			}

			// Below every threshold the original engine falls back to the last
			// (most disdainful) level.
			return levels[levels.Count - 1].chanceOfSuccessfulPropaganda;
		}

		// The civ's total culture, the sum of its cities' cultural value.
		public static int PlayerCulture(Player player) {
			int total = 0;
			foreach (City city in player.cities) {
				total += city.GetCultureFor(player);
			}
			return total;
		}

		// ---------------------------------------------------------------------
		// Running a mission (spec 4.4, 5.2, 6, 7)
		// ---------------------------------------------------------------------

		// Runs one mission attempt: checks the preconditions, pays the cost and
		// resolves the mission. The cost is paid before the roll, so a failed
		// mission still costs the full price.
		public static EspionageMissionResult RunMission(GameData gameData, Player actor, Player target, City targetCity, int missionId, EspionageAgent agent, EspionageSafetyLevel safety = EspionageSafetyLevel.Carefully) {
			EspionageMissionResult result = new();

			if (gameData?.GetEspionageMission(missionId) == null) {
				result.message = $"Unknown espionage mission id {missionId}.";
				return result;
			}
			if (!MissionIsAvailable(gameData, actor, target, missionId, agent)) {
				result.message = "The mission is not available.";
				return result;
			}
			if (targetCity == null || targetCity.owner != target) {
				result.message = "The target city does not belong to the target civ.";
				return result;
			}
			if (missionId == InitiatePropaganda) {
				// The per-citizen subversion roll and the city flip need city
				// capture and happiness machinery the engine does not have yet.
				result.message = "Initiate Propaganda has no engine effect yet.";
				return result;
			}

			int cost = MissionCost(gameData, actor, target, targetCity, missionId, safety);
			result.costPaid = cost;
			result.successChance = SuccessChance(gameData, actor, target, missionId, agent, safety);

			if (actor.gold < cost) {
				result.message = "Not enough gold.";
				return result;
			}

			actor.gold -= cost;
			result.ran = true;

			if (GameData.rng.Next(100) >= result.successChance) {
				return FailMission(actor, target, agent, missionId, result);
			}

			ApplyEffect(gameData, actor, target, targetCity, missionId, result);
			return result;
		}

		private static EspionageMissionResult FailMission(Player actor, Player target, EspionageAgent agent, int missionId, EspionageMissionResult result) {
			result.succeeded = false;
			if (missionId == PlantSpy) {
				// A caught plant attempt latches the target against any new
				// attempt.
				actor.playerRelationships[target.id].plantSpyAttemptWasCaught = true;
			}
			if (IsCatchableMission(missionId)) {
				NotifySpyCaught(actor, target, agent == EspionageAgent.Spy);
				result.agentCaught = true;
			}
			result.message = "The mission failed.";
			return result;
		}

		// The six missions whose failure runs the caught-spy bookkeeping
		// (spec 7). Embassy, investigate-city and propaganda never expose an
		// agent.
		private static bool IsCatchableMission(int missionId) {
			return missionId == StealTechnology || missionId == StealWorldMap
				|| missionId == PlantSpy || missionId == StealPlans
				|| missionId == SabotageProduction || missionId == ExposeEnemySpy;
		}

		// The target caught a spy. `actor` is the civ whose spy was caught and
		// `target` is the civ that caught it.
		private static void NotifySpyCaught(Player actor, Player target, bool agentWasSpy) {
			// The target's reputation counter for the actor goes up.
			target.playerRelationships[actor.id].caughtSpyCount++;

			// A caught spy is consumed: the actor must plant a new agent before
			// running any further spy mission against this civ.
			if (agentWasSpy) {
				actor.playerRelationships[target.id].agentPlanted = false;
			}
		}

		private static void ApplyEffect(GameData gameData, Player actor, Player target, City targetCity, int missionId, EspionageMissionResult result) {
			switch (missionId) {
				case BuildEmbassy: {
						// An embassy is mutual and reveals both capitals.
						actor.playerRelationships[target.id].embassyEstablished = true;
						target.playerRelationships[actor.id].embassyEstablished = true;
						City actorCapital = actor.cities.Find(city => city.IsCapital());
						City targetCapital = target.cities.Find(city => city.IsCapital());
						if (targetCapital != null) {
							RevealNeighborhood(actor, targetCapital.location);
						}
						if (actorCapital != null) {
							RevealNeighborhood(target, actorCapital.location);
						}
						result.succeeded = true;
						result.message = "Embassy established.";
						break;
					}
				case InvestigateCity:
					RevealNeighborhood(actor, targetCity.location);
					result.succeeded = true;
					result.message = "City investigated.";
					break;
				case StealTechnology: {
						if (GameData.rng.Next(100) >= StealTechnologySecondRoll) {
							result.harmlessFailure = true;
							result.message = "The agent escaped without stealing a technology.";
							break;
						}
						Tech stolen = FindStealableTech(gameData, actor, target);
						if (stolen == null) {
							result.harmlessFailure = true;
							result.message = "The target has no technology we can steal.";
							break;
						}
						actor.knownTechs.Add(stolen.id);
						result.stolenTech = stolen;
						result.succeeded = true;
						result.message = $"Stole {stolen.Name}.";
						break;
					}
				case StealWorldMap: {
						if (GameData.rng.Next(100) >= StealWorldMapSecondRoll) {
							result.harmlessFailure = true;
							result.message = "The agent escaped without the world map.";
							break;
						}
						foreach (Tile tile in target.tileKnowledge.AllKnownTiles()) {
							actor.tileKnowledge.AddTileToKnown(tile);
						}
						actor.tileKnowledge.RecomputeActiveTiles();
						result.succeeded = true;
						result.message = "Stole the world map.";
						break;
					}
				case PlantSpy:
					actor.playerRelationships[target.id].agentPlanted = true;
					result.succeeded = true;
					result.message = "Agent planted.";
					break;
				case StealPlans:
					// C3X's steal_plans_duration defaults to one turn; the original
					// engine's own expiry bookkeeping is not pinned by the spec
					// (24_espionage.md 10.5), so one turn is the documented stand-in.
					actor.playerRelationships[target.id].stolenPlansUntilTurn = gameData.turn + 1;
					result.succeeded = true;
					if (GameData.rng.Next(100) < StealPlansExposureRoll) {
						NotifySpyCaught(actor, target, true);
						result.agentCaught = true;
						result.message = "Stole plans, but the agent was caught.";
					} else {
						result.message = "Stole plans.";
					}
					break;
				case SabotageProduction: {
						int itemCost = targetCity.itemBeingProduced == null ? 0
						: targetCity.owner.ShieldCost(targetCity.itemBeingProduced);
						targetCity.SetStoredShields(Math.Min(targetCity.shieldsStored / 2, itemCost));
						result.succeeded = true;
						if (GameData.rng.Next(100) < SabotageExposureRoll) {
							NotifySpyCaught(actor, target, true);
							result.agentCaught = true;
							result.message = "Sabotaged production, but the agent was caught.";
						} else {
							result.message = "Sabotaged production.";
						}
						break;
					}
				case ExposeEnemySpy: {
						PlayerRelationship targetToActor = target.playerRelationships[actor.id];
						if (!targetToActor.agentPlanted) {
							// Nothing to expose; the attempt still runs the caught
							// bookkeeping.
							result.harmlessFailure = true;
							NotifySpyCaught(actor, target, true);
							result.agentCaught = true;
							result.message = "No enemy agent was found, and our agent was caught.";
							break;
						}
						targetToActor.agentPlanted = false;
						result.succeeded = true;
						if (GameData.rng.Next(100) < ExposeSpyExposureRoll) {
							NotifySpyCaught(actor, target, true);
							result.agentCaught = true;
							result.message = "Removed the enemy agent, but our agent was exposed.";
						} else {
							result.message = "Removed the enemy agent.";
						}
						break;
					}
			}
		}

		// The first technology in id order that the target knows, the actor does
		// not, and that is not hidden (the original engine skips era -1).
		//
		// This is the AI path. The original engine lets a human pick from the
		// target's technology list in the science advisor, which is UI work and
		// is left as a follow-up (24_espionage.md 10.7).
		private static Tech FindStealableTech(GameData gameData, Player actor, Player target) {
			foreach (Tech tech in gameData.techs) {
				if (tech.EraCivilopediaName == "Hidden") {
					continue;
				}
				if (!target.knownTechs.Contains(tech.id) || actor.knownTechs.Contains(tech.id)) {
					continue;
				}
				return tech;
			}
			return null;
		}

		// Reveals the 21-tile city radius around `center` to `viewer`. The guard
		// covers maps that are still being built, which have no tiles to look up.
		private static void RevealNeighborhood(Player viewer, Tile center) {
			if (center == null || center == Tile.NONE) {
				return;
			}
			viewer.tileKnowledge.AddTileToKnown(center);
			if (center.map?.tiles != null && center.map.tiles.Count > 0) {
				foreach (Tile tile in center.GetTilesWithinRankDistance(2)) {
					viewer.tileKnowledge.AddTileToKnown(tile);
				}
			}
			viewer.tileKnowledge.RecomputeActiveTiles();
		}

		private static bool TryGetRelationship(Player actor, Player target, out PlayerRelationship relationship) {
			relationship = null;
			if (actor == null || target == null || actor.id == target.id) {
				return false;
			}
			if (!actor.playerRelationships.TryGetValue(target.id, out relationship)) {
				return false;
			}
			return target.playerRelationships.ContainsKey(actor.id);
		}
	}
}
