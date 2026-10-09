using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using Xunit;

namespace EngineTests.GameData;

// Tests for the AI's espionage driver and its mood-weighted mission chooser
// (re/specs/24_espionage.md section 8.4).
//
// Every test here drives the game's own random source with a fixed or sequenced
// stub, so each outcome is concrete. The mood is built out of the diplomatic
// attitude model's own reputation counters rather than stubbed, so the tests
// fail if either the mood scale or the weight direction is inverted.
public class EspionageAiTest {
	// A pair of civs that have met, each with a capital, on a 100x100 map.
	//
	// `diplomatUnlocked` adds Writing (the only shipped technology that enables
	// the diplomat agent) and `spyUnlocked` adds the Intelligence Agency (the
	// only shipped wonder that allows spy missions), so a test can isolate one
	// agent's branch from the other's.
	private static (C7GameData.GameData gameData, Player actor, Player target, City actorCity, City targetCity) SetupPair(
			int actorGold = 100000, bool diplomatUnlocked = true, bool spyUnlocked = true) {
		C7GameData.GameData gameData = EspionageTestGame.NewGameData();
		Player actor = EspionageTestGame.NewPlayer(gameData, "actor", actorGold);
		Player target = EspionageTestGame.NewPlayer(gameData, "target", 100000);
		// The AI turn logs the civ's first city name.
		actor.civilization.cityNames.Add("Capital");
		target.civilization.cityNames.Add("Target");
		// The per-civ rules the player's own turn work reads.
		actor.rules = gameData.rules;
		target.rules = gameData.rules;
		gameData.players.Add(actor);
		gameData.players.Add(target);
		EspionageTestGame.Meet(actor, target);

		Tile actorTile = EspionageTestGame.NewTile(gameData, 0, 0);
		Tile targetTile = EspionageTestGame.NewTile(gameData, 8, 8);
		City actorCity = EspionageTestGame.NewCity(gameData, actor, actorTile, "Capital", 1, actor.civilization);
		actorCity.capital = true;
		City targetCity = EspionageTestGame.NewCity(gameData, target, targetTile, "Target", 6, target.civilization);
		targetCity.capital = true;

		// The AI turn recomputes citizen moods, which needs a citizen type on
		// every resident and enough content citizens to keep the cities out of
		// disorder.
		gameData.citizenTypes = new List<CitizenType> { new() { IsDefaultCitizen = true, SingularName = "Laborer" } };
		gameData.gameDifficulty.NumberOfCitizensBornContent = 6;
		foreach (City city in new[] { actorCity, targetCity }) {
			foreach (CityResident resident in city.residents) {
				resident.citizenType = gameData.citizenTypes[0];
			}
		}

		if (diplomatUnlocked) {
			EspionageTestGame.AddWriting(gameData, actor);
		}
		if (spyUnlocked) {
			actorCity.AddBuilding(EspionageTestGame.NewIntelligenceAgency(gameData));
		}
		return (gameData, actor, target, actorCity, targetCity);
	}

	// The two per-turn seams the engine's turn loop calls: the expiring state for
	// every player, then the driver for an AI player's own turn.
	private static void PlayEspionageTurn(C7GameData.GameData gameData, Player actor) {
		EspionageAi.BeginTurn(gameData, actor);
		EspionageAi.DoEspionage(gameData, actor);
	}

	private static PlayerRelationship Relationship(Player from, Player to) {
		return from.playerRelationships[to.id];
	}

	// Makes the actor's mood toward the target Gracious: the hostility score
	// starts at the aggression seed minus the ten-point peace term and the
	// subtracted military-aggression counter drives it below the Gracious
	// threshold (-10 on a 100x100 map).
	private static void MakeGracious(Player actor, Player target) {
		Relationship(actor, target).reputation.observedMilitaryAggression = 5;
	}

	// Makes the actor's mood toward the target Furious: one icbm memory is +32,
	// far above the +10 threshold.
	private static void MakeFurious(Player actor, Player target) {
		Relationship(actor, target).reputation.icbm = 1;
	}

	// Lets every mission the chooser's bucket table can pick pass its guards:
	// mission 2 wants the target to know more technologies, mission 3 wants a
	// tile only the target knows, and mission 8 wants an agent of the target
	// planted in the actor.
	private static void ArmEveryChooserGuard(C7GameData.GameData gameData, Player actor, Player target, City targetCity) {
		for (int i = 0; i < 4; i++) {
			target.knownTechs.Add(gameData.ids.CreateID("tech"));
		}
		target.tileKnowledge.AddTileToKnown(targetCity.location);
		Relationship(target, actor).agentPlanted = true;
	}

	// ---------------------------------------------------------------------
	// The weight table and its direction
	// ---------------------------------------------------------------------

	[Fact]
	public void MissionWeightsAreTheMeasuredTable() {
		Assert.Equal(200000, EspionageAi.MissionWeight(DiplomaticMood.Gracious));
		Assert.Equal(20000, EspionageAi.MissionWeight(DiplomaticMood.Polite));
		Assert.Equal(2000, EspionageAi.MissionWeight(DiplomaticMood.Cautious));
		Assert.Equal(200, EspionageAi.MissionWeight(DiplomaticMood.Angry));
		Assert.Equal(20, EspionageAi.MissionWeight(DiplomaticMood.Furious));

		// The weight is the bound of the draw and only its first ten values pick
		// a mission, so the chance of acting is 10 / weight. A bigger weight is
		// a smaller chance: the table is ordered from the least likely mood to
		// act (Gracious) to the most likely (Furious).
		Assert.True(EspionageAi.MissionWeight(DiplomaticMood.Gracious) > EspionageAi.MissionWeight(DiplomaticMood.Furious));
	}

	[Fact]
	public void TheMoodModelHasFiveLevelsSoASixthIsUnreachable() {
		// Every hostility score maps into the five levels, so the chooser's
		// out-of-range arm is a guard rather than a reachable state.
		HashSet<DiplomaticMood> seen = new();
		for (int score = -500; score <= 500; score++) {
			seen.Add(DiplomaticAttitude.MoodForScore(score, 100, 100));
		}
		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, seen.Select(mood => (int)mood).OrderBy(index => index).ToArray());

		// A mood outside the five levels weighs zero, and a zero weight never
		// tries.
		Assert.Equal(0, EspionageAi.MissionWeight((DiplomaticMood)5));
		Assert.Equal(0, EspionageAi.MissionWeight((DiplomaticMood)(-1)));

		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();
		MakeFurious(actor, target);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMissionForWeight(gameData, actor, target, EspionageAgent.Spy, 0));
	}

	// THE DIRECTION TEST. A Furious AI acts far more often than a Gracious one.
	// If the mood scale were inverted - if Gracious took the 20 weight and
	// Furious the 200000 - the two counts below would swap, and so would the
	// assertion. AnInvertedMoodTableWouldActMostAgainstFriends below runs that
	// inverted table explicitly and shows the swap.
	[Fact]
	public void AHostileAiActsFarMoreOftenThanAFriendlyOne() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		ArmEveryChooserGuard(gameData, actor, target, targetCity);

		MakeGracious(actor, target);
		Assert.Equal(DiplomaticMood.Gracious, DiplomaticAttitude.MoodToward(actor, target, gameData, suppressStrengthHalving: false));

		MakeFurious(actor, target);
		Assert.Equal(DiplomaticMood.Furious, DiplomaticAttitude.MoodToward(actor, target, gameData, suppressStrengthHalving: false));

		// The Gracious weight is 200000, so the chance of acting is 1 in 20000;
		// the Furious weight is 20, so it is 1 in 2.
		C7GameData.GameData.rng = new System.Random(20261013);
		int furiousRuns = 0;
		for (int turn = 0; turn < 200; turn++) {
			if (EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy) != EspionageAi.NoMission) {
				furiousRuns++;
			}
		}

		C7GameData.GameData.rng = new System.Random(20261013);
		MakeGracious(actor, target);
		// Undo the Furious counter, or the two moods would share a score.
		Relationship(actor, target).reputation.icbm = 0;
		Assert.Equal(DiplomaticMood.Gracious, DiplomaticAttitude.MoodToward(actor, target, gameData, suppressStrengthHalving: false));
		int graciousRuns = 0;
		for (int turn = 0; turn < 200; turn++) {
			if (EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy) != EspionageAi.NoMission) {
				graciousRuns++;
			}
		}

		Assert.True(furiousRuns > 50, $"a Furious AI acted in {furiousRuns} of 200 turns");
		Assert.Equal(0, graciousRuns);
	}

	// The falsification of the test above: run the same two moods through the
	// inverted table (Gracious weighted 20, Furious weighted 200000) and the
	// counts swap, which is what the assertion in
	// AHostileAiActsFarMoreOftenThanAFriendlyOne would then see.
	[Fact]
	public void AnInvertedMoodTableWouldActMostAgainstFriends() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		ArmEveryChooserGuard(gameData, actor, target, targetCity);

		MakeGracious(actor, target);
		C7GameData.GameData.rng = new System.Random(20261013);
		int graciousRuns = 0;
		for (int turn = 0; turn < 200; turn++) {
			if (EspionageAi.ChooseMissionForWeight(gameData, actor, target, EspionageAgent.Spy, 20) != EspionageAi.NoMission) {
				graciousRuns++;
			}
		}

		MakeFurious(actor, target);
		C7GameData.GameData.rng = new System.Random(20261013);
		int furiousRuns = 0;
		for (int turn = 0; turn < 200; turn++) {
			if (EspionageAi.ChooseMissionForWeight(gameData, actor, target, EspionageAgent.Spy, 200000) != EspionageAi.NoMission) {
				furiousRuns++;
			}
		}

		Assert.True(graciousRuns > 50, $"with the table inverted the Gracious AI acted {graciousRuns} times");
		Assert.Equal(0, furiousRuns);
	}

	// ---------------------------------------------------------------------
	// The chooser's ranges, guards and halving
	// ---------------------------------------------------------------------

	[Fact]
	public void TheChooserPicksMissionsByRange() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		ArmEveryChooserGuard(gameData, actor, target, targetCity);
		MakeFurious(actor, target);
		// A caught spy of the target halves the Furious weight to 10, so every
		// draw lands inside the ten buckets.
		Relationship(actor, target).reputation.caughtSpies = 1;

		// The buckets are two wide for missions 2, 6, 7 and 8 and one wide for
		// missions 3 and 5, so the mission ids are not contiguous.
		int[] expected = {
			Espionage.StealTechnology, Espionage.StealTechnology,
			Espionage.StealWorldMap,
			Espionage.StealPlans,
			Espionage.InitiatePropaganda, Espionage.InitiatePropaganda,
			Espionage.SabotageProduction, Espionage.SabotageProduction,
			Espionage.ExposeEnemySpy, Espionage.ExposeEnemySpy,
		};
		for (int roll = 0; roll < 10; roll++) {
			C7GameData.GameData.rng = new FixedRandom(roll);
			Assert.Equal(expected[roll], EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));
		}

		// A draw past the ten buckets is a turn on which the civ does nothing. The
		// halved weight is exactly ten, so this needs an unhalved one.
		Relationship(actor, target).reputation.caughtSpies = 0;
		C7GameData.GameData.rng = new FixedRandom(10);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMissionForWeight(gameData, actor, target, EspionageAgent.Spy, 20));
	}

	[Fact]
	public void TheWeightIsHalvedAtWarAndOnACaughtSpy() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		ArmEveryChooserGuard(gameData, actor, target, targetCity);
		MakeFurious(actor, target);

		// The Furious weight is 20, so a draw of 10 is outside the ten buckets.
		C7GameData.GameData.rng = new FixedRandom(10);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		// Halved to 10 the same draw is the first value, so a mission runs.
		C7GameData.GameData.rng = new FixedRandom(10);
		Assert.NotEqual(EspionageAi.NoMission, EspionageAi.ChooseMissionForWeight(gameData, actor, target, EspionageAgent.Spy, 10));

		// The halving fires when the actor has caught spies of the target ...
		Relationship(actor, target).reputation.caughtSpies = 1;
		C7GameData.GameData.rng = new FixedRandom(10);
		Assert.NotEqual(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		// ... and when the two are at war.
		Relationship(actor, target).reputation.caughtSpies = 0;
		PlayerRelationship.DeclareWar(actor, target, false, 0);
		Assert.True(PlayerRelationship.AtWar(actor, target));
		C7GameData.GameData.rng = new FixedRandom(10);
		Assert.NotEqual(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));
	}

	[Fact]
	public void TheChooserRefusesAnImmuneGovernmentPerMission() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		ArmEveryChooserGuard(gameData, actor, target, targetCity);
		MakeFurious(actor, target);
		Relationship(actor, target).reputation.caughtSpies = 1;

		// Bucket 4 is Initiate Propaganda. The shipped Democracy is immune to
		// mission 6 and to nothing else, and the chooser reads the field for
		// every mission it can pick.
		target.government = new Government { immuneTo = 6 };
		C7GameData.GameData.rng = new FixedRandom(4);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		// Bucket 6 is Sabotage Production, which the same government is not
		// immune to.
		C7GameData.GameData.rng = new FixedRandom(6);
		Assert.Equal(Espionage.SabotageProduction, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		// Immunity to mission 7 refuses the same bucket, and immunity to
		// mission 8 refuses bucket 8.
		target.government = new Government { immuneTo = 7 };
		C7GameData.GameData.rng = new FixedRandom(6);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		target.government = new Government { immuneTo = 8 };
		C7GameData.GameData.rng = new FixedRandom(8);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));
	}

	[Fact]
	public void TheChooserRefusesMissionsTheActingAgentMayNotRun() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		ArmEveryChooserGuard(gameData, actor, target, targetCity);

		// Bucket 3 is Steal Plans, which only a spy may run; a diplomat's agent
		// mask refuses it.
		C7GameData.GameData.rng = new FixedRandom(3);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMissionForWeight(gameData, actor, target, EspionageAgent.Diplomat, 10));

		// Bucket 0 is Steal Technology, which both agents may run.
		C7GameData.GameData.rng = new FixedRandom(0);
		Assert.Equal(Espionage.StealTechnology, EspionageAi.ChooseMissionForWeight(gameData, actor, target, EspionageAgent.Diplomat, 10));
	}

	[Fact]
	public void TheChooserRefusesTechnologyAndMapMissionsWithNothingToTake() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		MakeFurious(actor, target);
		Relationship(actor, target).reputation.caughtSpies = 1;

		// Nothing to steal: the actor knows every technology the target does and
		// every tile the target knows.
		target.tileKnowledge.AddTileToKnown(targetCity.location);
		actor.tileKnowledge.AddTileToKnown(targetCity.location);
		C7GameData.GameData.rng = new FixedRandom(0);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		C7GameData.GameData.rng = new FixedRandom(2);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		// One technology and one tile the actor does not know are enough. The
		// actor knows Writing, so the target needs two technologies to know more.
		target.knownTechs.Add(gameData.ids.CreateID("tech"));
		target.knownTechs.Add(gameData.ids.CreateID("tech"));
		Tile unknown = EspionageTestGame.NewTile(gameData, 20, 20);
		target.tileKnowledge.AddTileToKnown(unknown);
		C7GameData.GameData.rng = new FixedRandom(0);
		Assert.Equal(Espionage.StealTechnology, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		C7GameData.GameData.rng = new FixedRandom(2);
		Assert.Equal(Espionage.StealWorldMap, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));
	}

	[Fact]
	public void TheChooserRefusesToExposeAnAgentThatIsNotThere() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		ArmEveryChooserGuard(gameData, actor, target, targetCity);
		MakeFurious(actor, target);
		Relationship(actor, target).reputation.caughtSpies = 1;

		// Bucket 8 with the target's agent in place.
		C7GameData.GameData.rng = new FixedRandom(8);
		Assert.Equal(Espionage.ExposeEnemySpy, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));

		// With no agent of the target planted in the actor, the same bucket is
		// refused.
		Relationship(target, actor).agentPlanted = false;
		C7GameData.GameData.rng = new FixedRandom(8);
		Assert.Equal(EspionageAi.NoMission, EspionageAi.ChooseMission(gameData, actor, target, EspionageAgent.Spy));
	}

	// ---------------------------------------------------------------------
	// The driver: the embassy branch
	// ---------------------------------------------------------------------

	[Fact]
	public void TheEmbassyBranchRequiresAllFourConditions() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();
		C7GameData.GameData.rng = new FixedRandom(0);

		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).embassyEstablished);
		Assert.True(Relationship(target, actor).embassyEstablished);
		Assert.True(actor.gold < 100000);
	}

	[Fact]
	public void TheEmbassyBranchDoesNotRunAtWar() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();
		PlayerRelationship.DeclareWar(actor, target, false, 0);
		C7GameData.GameData.rng = new FixedRandom(0);

		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).embassyEstablished);
		Assert.Equal(100000, actor.gold);
	}

	[Fact]
	public void TheEmbassyBranchDoesNotRunWithoutTheDiplomatAgent() {
		// Writing is the only shipped technology that enables the diplomat
		// agent.
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair(diplomatUnlocked: false);
		C7GameData.GameData.rng = new FixedRandom(0);

		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).embassyEstablished);
		Assert.Equal(100000, actor.gold);
	}

	[Fact]
	public void TheEmbassyBranchDoesNotRunWithoutATargetCapital() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair();
		targetCity.capital = false;
		C7GameData.GameData.rng = new FixedRandom(0);

		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).embassyEstablished);
		Assert.Equal(100000, actor.gold);
	}

	[Fact]
	public void TheEmbassyReserveIsSixteenGoldPerCityAndRisesWithTheWonderCount() {
		(C7GameData.GameData gameData, Player actor, Player target, City actorCity, _) = SetupPair();

		// One city and no wonder with the reserve flag: the reserve is 16 and
		// the treasury must be strictly above it.
		Assert.Equal(16, EspionageAi.EspionageReserve(actor, EspionageAi.EmbassyReservePerCity));
		actor.gold = 16;
		Assert.False(EspionageAi.TreasuryExceedsReserve(actor, EspionageAi.EmbassyReservePerCity));
		actor.gold = 17;
		Assert.True(EspionageAi.TreasuryExceedsReserve(actor, EspionageAi.EmbassyReservePerCity));

		// Wall Street is the only shipped building carrying the flag, and it is
		// a small wonder: the reserve rises by 1000 divided by the count.
		actorCity.AddBuilding(new Building(new SaveBuilding {
			name = "Wall Street",
			isSmallWonder = true,
			flags = { SaveBuilding.Flag.TreasuryEarnsInterest },
		}, gameData));
		Assert.Equal(1016, EspionageAi.EspionageReserve(actor, EspionageAi.EmbassyReservePerCity));
		actor.gold = 1016;
		Assert.False(EspionageAi.TreasuryExceedsReserve(actor, EspionageAi.EmbassyReservePerCity));
		actor.gold = 1017;
		Assert.True(EspionageAi.TreasuryExceedsReserve(actor, EspionageAi.EmbassyReservePerCity));

		// The driver honours the reserve: ten cities raise it to 1160, and a
		// treasury at the reserve is not enough.
		for (int i = 0; i < 9; i++) {
			EspionageTestGame.NewCity(gameData, actor, EspionageTestGame.NewTile(gameData, 50 + i, 50 + i), $"City{i}", 1, actor.civilization);
		}
		Assert.Equal(1160, EspionageAi.EspionageReserve(actor, EspionageAi.EmbassyReservePerCity));

		actor.gold = 1160;
		C7GameData.GameData.rng = new FixedRandom(0);
		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).embassyEstablished);

		actor.gold = 1161;
		C7GameData.GameData.rng = new FixedRandom(0);
		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).embassyEstablished);
	}

	[Fact]
	public void TheEmbassyIsDelayedAgainstAHumanTarget() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();
		target.isHuman = true;

		// Against a human the branch first passes one draw out of 64.
		C7GameData.GameData.rng = new FixedRandom(1);
		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).embassyEstablished);

		C7GameData.GameData.rng = new FixedRandom(0);
		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).embassyEstablished);

		// Against a non-human civ the same draw value does not stop it, because
		// there is no draw at all.
		Relationship(actor, target).embassyEstablished = false;
		Relationship(target, actor).embassyEstablished = false;
		target.isHuman = false;
		C7GameData.GameData.rng = new FixedRandom(1);
		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).embassyEstablished);
	}

	// ---------------------------------------------------------------------
	// The driver: the plant-spy branch
	// ---------------------------------------------------------------------

	// The plant-spy draw is one out of sixteen at peace and one out of four at
	// war. A FixedRandom of 12 therefore does NOT plant at peace - which is what
	// falsifies a one-in-twelve divisor - while the same draw value does plant
	// at war.
	[Fact]
	public void ThePlantSpyRollIsOneInSixteenAtPeaceAndOneInFourAtWar() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair(diplomatUnlocked: false);
		target.isHuman = true;
		// The shipped Democracy is immune to Initiate Propaganda, so a successful
		// plant cannot be followed in the same turn by the propaganda mission the
		// chooser branch would otherwise pick for these draw values.
		target.government = new Government { immuneTo = 6 };

		// At peace: draw 0 plants, draw 4 and draw 12 do not.
		C7GameData.GameData.rng = new FixedRandom(0);
		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).agentPlanted);

		Relationship(actor, target).agentPlanted = false;
		C7GameData.GameData.rng = new FixedRandom(4);
		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).agentPlanted);

		C7GameData.GameData.rng = new FixedRandom(12);
		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).agentPlanted);

		// At war: the divisor is four, so the same two draws both plant.
		PlayerRelationship.DeclareWar(actor, target, false, 0);
		C7GameData.GameData.rng = new FixedRandom(4);
		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).agentPlanted);

		Relationship(actor, target).agentPlanted = false;
		C7GameData.GameData.rng = new FixedRandom(12);
		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).agentPlanted);
	}

	[Fact]
	public void ThePlantSpyBranchNeedsTheSpyAgentAndTheTargetCapital() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair(diplomatUnlocked: false, spyUnlocked: false);
		target.isHuman = true;
		C7GameData.GameData.rng = new FixedRandom(0);

		// Without the Intelligence Agency the spy agent is unavailable.
		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.Equal(100000, actor.gold);

		// With the wonder but with a target that owns no capital, there is no
		// city to plant into.
		actor.cities[0].AddBuilding(EspionageTestGame.NewIntelligenceAgency(gameData));
		targetCity.capital = false;
		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.Equal(100000, actor.gold);
	}

	[Fact]
	public void ThePlantSpyBranchOnlyRunsAgainstAHumanTarget() {
		// The spy branches test the target against the game's local-human mask,
		// so an AI target never gets an agent planted even with a draw that
		// would plant.
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair(diplomatUnlocked: false);
		Assert.False(target.isHuman);
		C7GameData.GameData.rng = new FixedRandom(0);

		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.Equal(100000, actor.gold);
	}

	[Fact]
	public void TheCaughtPlantLatchSuppressesASecondPlant() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair(diplomatUnlocked: false);
		target.isHuman = true;
		Relationship(actor, target).plantSpyAttemptWasCaught = true;

		// A draw that would plant does nothing while the latch is set.
		C7GameData.GameData.rng = new FixedRandom(0);
		EspionageAi.DoEspionage(gameData, actor);
		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.Equal(100000, actor.gold);
	}

	[Fact]
	public void TheCaughtPlantLatchIsClearedByOneDrawInThreeAndOnlyWhenItIsSet() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair(diplomatUnlocked: false);
		target.isHuman = true;

		// The latch clear runs before the driver and draws only when the latch
		// is set, so the same two-value sequence plants when the latch is clear
		// (the first value is the plant draw) and does not when it is set (the
		// first value clears the latch and the second is the plant draw).
		Relationship(actor, target).plantSpyAttemptWasCaught = false;
		C7GameData.GameData.rng = new SequenceRandom(0, 5, 9);
		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).agentPlanted);

		Relationship(actor, target).agentPlanted = false;
		Relationship(actor, target).plantSpyAttemptWasCaught = true;
		C7GameData.GameData.rng = new SequenceRandom(0, 5, 9);
		PlayEspionageTurn(gameData, actor);
		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.False(Relationship(actor, target).plantSpyAttemptWasCaught);

		// A draw that is not zero leaves the latch in place.
		Relationship(actor, target).plantSpyAttemptWasCaught = true;
		C7GameData.GameData.rng = new FixedRandom(1);
		PlayEspionageTurn(gameData, actor);
		Assert.True(Relationship(actor, target).plantSpyAttemptWasCaught);
	}

	// ---------------------------------------------------------------------
	// The driver: the spy-mission chooser branch and the per-turn hook
	// ---------------------------------------------------------------------

	[Fact]
	public void TheDriverRunsAChosenSpyMissionAgainstAPlantedAgent() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair(diplomatUnlocked: false);
		target.isHuman = true;
		ArmEveryChooserGuard(gameData, actor, target, targetCity);
		MakeFurious(actor, target);
		Relationship(actor, target).reputation.caughtSpies = 1;
		Relationship(actor, target).agentPlanted = true;
		Relationship(target, actor).agentPlanted = true;

		// Bucket 8 exposes the target's agent, the odds roll passes and the
		// exposure roll does not fire.
		C7GameData.GameData.rng = new SequenceRandom(8, 0, 50);
		PlayEspionageTurn(gameData, actor);

		Assert.False(Relationship(target, actor).agentPlanted);
		Assert.True(Relationship(actor, target).agentPlanted);
		Assert.True(actor.gold < 100000);
	}

	[Fact]
	public void TheDriverDoesNotRunSpyMissionsWithoutAPlantedAgent() {
		(C7GameData.GameData gameData, Player actor, Player target, _, City targetCity) = SetupPair(diplomatUnlocked: false);
		target.isHuman = true;
		ArmEveryChooserGuard(gameData, actor, target, targetCity);
		MakeFurious(actor, target);
		Relationship(actor, target).reputation.caughtSpies = 1;
		// The latch stops the plant attempt, so no agent is planted and the
		// chooser branch is never reached.
		Relationship(actor, target).plantSpyAttemptWasCaught = true;

		C7GameData.GameData.rng = new SequenceRandom(1, 8, 0, 50);
		PlayEspionageTurn(gameData, actor);

		Assert.False(Relationship(actor, target).agentPlanted);
		Assert.Equal(100000, actor.gold);
	}

	[Fact]
	public void TheDriverSkipsCivsTheActorHasNotMet() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();
		// A third civ the actor has never met: no relationship either way.
		Player stranger = EspionageTestGame.NewPlayer(gameData, "stranger", 100000);
		gameData.players.Add(stranger);
		C7GameData.GameData.rng = new FixedRandom(0);

		PlayEspionageTurn(gameData, actor);

		Assert.True(Relationship(actor, target).embassyEstablished);
		Assert.False(actor.playerRelationships.ContainsKey(stranger.id));
		Assert.False(stranger.playerRelationships.ContainsKey(actor.id));
	}

	// The other half of the hook: the per-civ AI turn returns immediately for a
	// human civ, so the driver never runs for one.
	[Fact]
	public async System.Threading.Tasks.Task TheAiTurnDoesNotDriveAHumanCiv() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();
		actor.isHuman = true;
		C7GameData.GameData.rng = new FixedRandom(0);

		await PlayerAI.PlayTurn(actor, gameData);

		Assert.False(Relationship(actor, target).embassyEstablished);
		Assert.Equal(100000, actor.gold);
	}

	// The hook: the per-civ AI turn runs the espionage work, so a civ that meets
	// the embassy conditions establishes one during its own turn.
	[Fact]
	public async System.Threading.Tasks.Task TheAiTurnRunsTheEspionageDriver() {
		(C7GameData.GameData gameData, Player actor, Player target, _, _) = SetupPair();
		// The AI turn's slider adjustment reassigns citizens, which needs a map
		// this fixture does not build; an empty city keeps it out of that path.
		actor.cities[0].residents.Clear();
		C7GameData.GameData.rng = new FixedRandom(0);

		await PlayerAI.PlayTurn(actor, gameData);

		Assert.True(Relationship(actor, target).embassyEstablished);
	}
}
