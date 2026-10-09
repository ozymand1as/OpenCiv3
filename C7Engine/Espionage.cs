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
	// started (unknown mission, unavailable, invalid city, or not enough gold),
	// in which case no gold was spent.
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
	// the success-probability formula and the per-mission resolutions,
	// including Initiate Propaganda's per-citizen subversion roll and city flip
	// (spec 6.6).
	//
	// Not implemented here: the AI's per-turn espionage driver and its
	// mood-weighted mission chooser (no AI espionage hook and no
	// diplomatic-mood field - 24_espionage.md 8.4), and the human
	// steal-technology pick through the science advisor (24_espionage.md 6.3,
	// marked [?] in the spec).
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

		// Initiate Propaganda flips the city when this share of its population
		// has been subverted (spec 6.6 steps 3 and 4).
		private const int PropagandaRevoltThreshold = 75;

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

		// Whether the acting civ may currently use `agent` at all (spec 3.1,
		// 3.2). The original keeps the two predicates in separate vtable slots
		// of the two agent records; a mission may only be offered, and only run,
		// by an agent the civ has. A mission whose ESPN mask admits the agent
		// kind is still refused when this predicate says no.
		public static bool AgentIsAvailable(GameData gameData, Player actor, EspionageAgent agent) {
			return agent == EspionageAgent.Diplomat
				? DiplomatIsAvailable(gameData, actor)
				: SpyIsAvailable(gameData, actor);
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
			if (!AgentIsAvailable(gameData, actor, agent)) {
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

		// The percentage the mission's first roll is compared against (spec 5.1).
		// Missions 0 and 1 always pass it; mission 6 has no first roll at all -
		// it resolves per citizen instead (spec 6.6), so the value it reports is
		// the agent record's base_chance, not the roll the mission runs.
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
		// the actor's culture ratio against the target (spec 6.6): the row whose
		// CultureRatioPercentage is the largest value not exceeding the ratio,
		// falling back to the lowest-threshold (default) row when the ratio is
		// below every threshold. The selection does not depend on the order the
		// rows are listed in.
		public static int PropagandaChance(GameData gameData, Player actor, Player target) {
			List<CultureLevel> levels = gameData?.cultureLevels;
			if (levels == null || levels.Count == 0) {
				return 0;
			}

			long ratio;
			int ownCulture = PlayerCulture(actor);
			int targetCulture = PlayerCulture(target);
			if (ownCulture <= 0) {
				ratio = 0;
			} else if (targetCulture <= 0) {
				ratio = long.MaxValue;
			} else {
				ratio = 100L * ownCulture / targetCulture;
			}

			int chosen = -1;
			int chosenThreshold = int.MinValue;
			int lowest = 0;
			int lowestThreshold = int.MaxValue;
			for (int i = 0; i < levels.Count; i++) {
				int threshold = levels[i].cultureRatioPercentage;
				if (threshold <= ratio && threshold > chosenThreshold) {
					chosen = i;
					chosenThreshold = threshold;
				}
				if (threshold < lowestThreshold) {
					lowest = i;
					lowestThreshold = threshold;
				}
			}
			return levels[chosen >= 0 ? chosen : lowest].chanceOfSuccessfulPropaganda;
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
			// The run path composes the agent predicate again, so a caller that
			// bypasses the mission menu still cannot run a mission through an
			// agent its civ does not have.
			if (!AgentIsAvailable(gameData, actor, agent)) {
				result.message = "The acting civ cannot use that agent.";
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

			int cost = MissionCost(gameData, actor, target, targetCity, missionId, safety);
			result.costPaid = cost;
			result.successChance = SuccessChance(gameData, actor, target, missionId, agent, safety);

			if (actor.gold < cost) {
				result.message = "Not enough gold.";
				return result;
			}

			actor.gold -= cost;
			result.ran = true;

			if (missionId == InitiatePropaganda) {
				// Mission 6 has no single success roll: spec 6.6 resolves it
				// entirely through per-citizen subversion rolls over the
				// penalty total, so the generic odds roll below is skipped.
				ResolvePropaganda(gameData, actor, target, targetCity, result);
				return result;
			}

			if (GameData.rng.Next(100) >= result.successChance) {
				return FailMission(gameData, actor, target, agent, missionId, result);
			}

			ApplyEffect(gameData, actor, target, targetCity, missionId, result);
			return result;
		}

		// ---------------------------------------------------------------------
		// Initiate propaganda (spec 6.6)
		// ---------------------------------------------------------------------

		// The penalty total D every per-citizen roll adds (spec 6.6 step 1).
		public static int PropagandaPenalty(GameData gameData, Player actor, Player target, City targetCity) {
			if (targetCity == null) {
				return 0;
			}
			int penalty = -5 * MilitaryUnitCount(targetCity);
			if (targetCity.IsCapital()) {
				penalty -= 40;
			}
			if (targetCity.GetBuildings().Any(cb => cb.building.resistantToBribery)) {
				penalty -= 20;
			}
			if (targetCity.isWeLoveTheKingDay) {
				penalty -= 10;
			}
			if (targetCity.isInCivilDisorder) {
				penalty += 10;
			}
			return penalty + PropagandaGovernmentModifier(gameData, actor, target);
		}

		// Military units at the city. The original's counter asks for attack or
		// defence strength greater than zero, so civilians on the tile (workers,
		// settlers) do not stiffen the garrison.
		private static int MilitaryUnitCount(City targetCity) {
			if (targetCity.location == null) {
				return 0;
			}
			return targetCity.location.unitsOnTile.Count(u =>
				u.unitType != null && (u.unitType.attack > 0 || u.unitType.defense > 0));
		}

		// The M term of the penalty: the acting government's propaganda (or
		// bribery) modifier against the target's government - the acting
		// government's row of the GOVT_GOVT BriberyModifier matrix, column =
		// the target government's position in the government list (spec 6.6
		// step 1). A government pair the list does not hold reads no modifier.
		private static int PropagandaGovernmentModifier(GameData gameData, Player actor, Player target) {
			if (gameData == null || actor?.government == null || target?.government == null) {
				return 0;
			}
			int own = gameData.governments.IndexOf(actor.government);
			int theirs = gameData.governments.IndexOf(target.government);
			if (own < 0 || theirs < 0) {
				return 0;
			}
			return gameData.governments[own].BriberyModifierAgainst(theirs);
		}

		// One citizen's subversion chance (spec 6.6 step 2): the culture
		// level's base chance plus the penalty total, plus 20 when the citizen
		// is of the acting civ's nationality, clamped to 0..95.
		public static int PropagandaCitizenChance(int baseChance, int penaltyTotal, bool citizenIsActorNational) {
			return Math.Clamp(baseChance + penaltyTotal + (citizenIsActorNational ? 20 : 0), 0, 95);
		}

		// The per-citizen resolution of mission 6 (spec 6.6): roll every
		// citizen of the target city against its own chance and count the
		// passes as S. Below 75% of the population the city merely boils; at or
		// above it the city revolts and changes hands through the ordinary
		// city-capture path. Both branches raise the city's propaganda-face
		// counter to max(old, S) and recompute happiness. The agent is never
		// exposed (spec 6.6, 7).
		private static void ResolvePropaganda(GameData gameData, Player actor, Player target, City targetCity, EspionageMissionResult result) {
			int population = targetCity.residents.Count;
			int baseChance = PropagandaChance(gameData, actor, target);
			int penalty = PropagandaPenalty(gameData, actor, target, targetCity);
			int subverted = 0;
			foreach (CityResident resident in targetCity.residents) {
				int citizenChance = PropagandaCitizenChance(baseChance, penalty, resident.nationality == actor.civilization);
				if (GameData.rng.Next(100) < citizenChance) {
					subverted++;
				}
			}

			bool revolt = population > 0 && subverted * 100 / population >= PropagandaRevoltThreshold;
			if (revolt) {
				CityInteractions.CaptureCity(targetCity, actor);
			}

			// The same happiness/propaganda-face update runs either way (spec
			// 6.6 steps 3 and 4): the counter holds max(old, S), and the
			// recompute subtracts those faces from the happy-face total.
			targetCity.unhappyFacesDueToPropaganda = Math.Max(targetCity.unhappyFacesDueToPropaganda, subverted);
			targetCity.RecalculateCitizenMoods(gameData);

			result.succeeded = revolt;
			result.message = revolt
				? "The city has revolted and joined us."
				: "The propaganda missed; the city only boils.";
		}

		private static EspionageMissionResult FailMission(GameData gameData, Player actor, Player target, EspionageAgent agent, int missionId, EspionageMissionResult result) {
			result.succeeded = false;
			if (missionId == PlantSpy) {
				// A caught plant attempt latches the target against any new
				// attempt.
				actor.playerRelationships[target.id].plantSpyAttemptWasCaught = true;
			}
			if (IsCatchableMission(missionId)) {
				NotifySpyCaught(gameData, actor, target, agent == EspionageAgent.Spy);
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
		private static void NotifySpyCaught(GameData gameData, Player actor, Player target, bool agentWasSpy) {
			// The target's reputation counter for the actor goes up.
			target.playerRelationships[actor.id].caughtSpyCount++;

			// The same event bumps the target's diplomatic memory of the actor:
			// one caught spy (reputation index 4), the provocation handler at
			// `0x502cc0`.
			target.playerRelationships[actor.id].reputation.RecordSpyCaught();

			// A caught spy is consumed: the actor must plant a new agent before
			// running any further spy mission against this civ.
			if (agentWasSpy) {
				actor.playerRelationships[target.id].agentPlanted = false;
			}

			// The civ that caught the spy declares war on the spy's owner when
			// that civ is not the human player and the two are still at peace
			// (spec 7, step 3). The original also consults an AI willingness
			// predicate; the engine has no diplomatic-mood field to weight
			// (24_espionage.md 8.4), so an AI still at peace responds with war.
			if (!target.isHuman && PlayerRelationship.AtPeace(actor, target)) {
				target.DeclareWarOn(actor, gameData.turn);
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
						NotifySpyCaught(gameData, actor, target, true);
						result.agentCaught = true;
						result.message = "Stole plans, but the agent was caught.";
					} else {
						result.message = "Stole plans.";
					}
					break;
				case SabotageProduction: {
						// Halve the production box, never leaving more shields in it
						// than the item in production costs. A city with nothing in
						// production has no item cost to cap against, so the box is
						// halved rather than emptied (spec 6.7).
						int halved = targetCity.shieldsStored / 2;
						int itemCost = targetCity.itemBeingProduced == null ? int.MaxValue
							: targetCity.owner.ShieldCost(targetCity.itemBeingProduced);
						targetCity.SetStoredShields(Math.Min(halved, itemCost));
						result.succeeded = true;
						if (GameData.rng.Next(100) < SabotageExposureRoll) {
							NotifySpyCaught(gameData, actor, target, true);
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
							NotifySpyCaught(gameData, actor, target, true);
							result.agentCaught = true;
							result.message = "No enemy agent was found, and our agent was caught.";
							break;
						}
						targetToActor.agentPlanted = false;
						result.succeeded = true;
						if (GameData.rng.Next(100) < ExposeSpyExposureRoll) {
							NotifySpyCaught(gameData, actor, target, true);
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
