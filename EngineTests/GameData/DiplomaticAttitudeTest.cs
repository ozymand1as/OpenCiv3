using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;
using static C7GameData.PlayerRelationship;

namespace EngineTests.GameData;

// Tests for the diplomatic attitude model (spec 20_diplomacy_trade.md sections
// 3 and 4) and its per-turn drift (spec 10_turn_sequence.md section 3 step 4).
//
// SIGN CONVENTION under test: the score is HOSTILITY. Positive = the observer
// dislikes the subject. Every expected value below is derived from that, so an
// inverted scale fails these tests.
public class DiplomaticAttitudeTest {
	// A pair of civs on a map of known size, at peace, with no history. Player A
	// is culture group 0 and B is group 1 so the culture-group term is zero.
	private static (C7GameData.GameData gd, Player a, Player b) NewPair(
			int width = 100, int height = 100, int seedA = 0, int seedB = 0) {
		C7GameData.GameData gd = new(1);
		gd.map.numTilesWide = width;
		gd.map.numTilesTall = height;
		gd.rules = new Rules();

		Player a = NewPlayer(gd, "A", seedA, 0);
		Player b = NewPlayer(gd, "B", seedB, 1);
		gd.players.Add(a);
		gd.players.Add(b);
		EngineStorage.InitializeGameDataForTests(gd);

		a.EnsureRelationshipExists(b);
		return (gd, a, b);
	}

	private static Player NewPlayer(C7GameData.GameData gd, string name, int aggression, int cultureGroup) {
		Civilization civ = new Civilization(name) { aggressionLevel = aggression };
		civ.SetCultureGroup(cultureGroup, "Culture" + cultureGroup);
		return new Player {
			id = gd.ids.CreateID("player"),
			civilization = civ,
			government = new Government { name = "Despotism" },
		};
	}

	private static PlayerRelationship Relationship(Player from, Player to) {
		return from.playerRelationships[to.id];
	}

	private static int Score(Player a, Player b, C7GameData.GameData gd, bool suppressStrengthHalving = false) {
		return DiplomaticAttitude.HostilityScore(a, b, gd, suppressStrengthHalving);
	}

	// ------------------------------------------------------------------
	// Initial value, sign and range
	// ------------------------------------------------------------------

	// A fresh pair starts with all nineteen reputation counters at zero, so the
	// score is the aggression seed minus the ten-point peace term. Positive =
	// hostile: the value MUST be negative (friendly) for two civs that have just
	// met, or every consumer of the score is inverted.
	[Fact]
	public void FreshPeacefulPairHasAZeroedReputationAndAFriendlyScore() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		Assert.Equal(0, Relationship(a, b).reputation.warDeclarationsAgainstUs);
		Assert.Equal(0, Relationship(a, b).reputation.caughtSpies);
		Assert.Equal(0, Relationship(a, b).reputation.icbm);
		Assert.Equal(0, Relationship(a, b).reputation.field3C3);

		Assert.Equal(-10, Score(a, b, gd));
		Assert.Equal(DiplomaticMood.Polite, DiplomaticAttitude.MoodToward(a, b, gd, suppressStrengthHalving: false));
	}

	// The seed is A's own aggression level, so the same state gives A a
	// different opinion of B than B has of A (the two directions are
	// independent in the original too).
	[Fact]
	public void TheAggressionSeedIsPerObserver() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair(seedA: 2, seedB: -2);

		Assert.Equal(2 - 10, Score(a, b, gd));
		Assert.Equal(-2 - 10, Score(b, a, gd));
	}

	[Fact]
	public void HostilityIsClampedToHalfTheMapPerimeterAtBothEnds() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair(width: 20, height: 20);

		// +32 per icbm; 4 of them = +128, far past the +20 clamp.
		Relationship(a, b).reputation.icbm = 4;
		Assert.Equal(20, Score(a, b, gd));

		// The subtracted counter is uncapped, so it drives the score past the
		// lower clamp.
		Relationship(a, b).reputation.icbm = 0;
		Relationship(a, b).reputation.observedMilitaryAggression = 100;
		Assert.Equal(-20, Score(a, b, gd));
	}

	// ------------------------------------------------------------------
	// The 19 weighted reputation terms (spec section 3.1)
	// ------------------------------------------------------------------

	// Each entry of the reputation record has its own weight and cap. The
	// expected total below is the sum of section 3.1's table applied to the raw
	// counters, including index 0's *uncapped* weight (the disassembly at
	// 0x440135 scales it by four without the clamp the next field has).
	[Fact]
	public void ReputationContributionUsesTheSpecifiedWeightAndCapPerField() {
		DiplomaticReputation rep = new DiplomaticReputation {
			warDeclarationsAgainstUs = 3,     // +4 x 3 (no cap)
			cancelledDeals = 3,               // +min(4x3, 8) = 8
			trustCounter = 3,                 // +min(8x3, 16) = 16
			aggressionObserved = 3,           // +min(2x3, 4) = 4
			caughtSpies = 3,                  // +4 x 3
			field14 = 20,                     // +min(20, 10)
			tribute = 3,                      // +2 x 3
			observedMilitaryAggression = 3,   // -3
			diplomaticThreats = 3,            // +3
			gifts = 100,                      // -min(100/10, 10) = -10
			refusedTributeDemands = 1,        // -(>0 ? 1 : 0) = -1
			icbm = 1,                         // +32
			icbmOther = 1,                    // +16
			warDamageMemory = 20,             // +min(20, 10)
			warDamageThisTurn = 20,           // +min(20, 10)
			field3C0 = 20,                    // -min(20, 10)
			field3C1 = 1,                     // +16
			field3C2 = 1,                     // +1
			field3C3 = 1,                     // +1
		};

		int expected = 4 * 3 + 8 + 16 + 4 + 4 * 3 + 10 + 2 * 3 - 3 + 3 - 10 - 1
			+ 32 + 16 + 10 + 10 - 10 + 16 + 1 + 1;
		Assert.Equal(expected, rep.HostilityContribution());
	}

	// The embassy flag (the header's D50 array) makes A friendlier while at
	// peace (-2) and *less* friendly once the two are at war (+1).
	[Fact]
	public void EmbassyShiftsHostilityByDirection() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		Relationship(a, b).embassyEstablished = true;
		Assert.Equal(-12, Score(a, b, gd));

		Relationship(a, b).embassyEstablished = false;
		DeclareWar(a, b, sneakAttack: false, refuseContactUntilTurn: 10);
		Relationship(a, b).embassyEstablished = true;
		Assert.Equal(6, Score(a, b, gd));
	}

	// A peace or MPP treaty is worth -10, a military alliance a further -5.
	// Peace is a multi-turn deal in this engine, so "not at war" is the peace
	// half of the term.
	[Fact]
	public void PeaceTreatyLowersHostilityAndWarRemovesIt() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		Assert.Equal(-10, Score(a, b, gd));

		DeclareWar(a, b, sneakAttack: false, refuseContactUntilTurn: 10);
		// At war: the -10 peace term is gone and the +5 war term appears.
		Assert.Equal(5, Score(a, b, gd));
	}

	[Fact]
	public void MutualProtectionPactAndMilitaryAllianceEachLowerHostility() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		RegisterMultiTurnDeal(a, b, new MultiTurnDeal(DealType.DiplomaticAgreement,
			DealSubType.MutualProtectionPact, DealDetails.Exchange, 0, null, 0, 0, null));
		Assert.Equal(-10, Score(a, b, gd));

		RegisterMultiTurnDeal(a, b, new MultiTurnDeal(DealType.DiplomaticAgreement,
			DealSubType.MilitaryAlliance, DealDetails.Exchange, 0, null, 0, 0, null));
		Assert.Equal(-15, Score(a, b, gd));
	}

	// The government term: a shared government that is the observer's race's
	// favourite is worth -5, and the other civ running the race's shunned
	// government is worth +5.
	[Fact]
	public void GovernmentComparisonsUseTheObserversRacePreferences() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		a.civilization.favoriteGovernmentName = "Despotism";
		a.civilization.shunnedGovernmentName = "Republic";

		// Same government, and it is the favourite: -5.
		Assert.Equal(-15, Score(a, b, gd));

		// Same government but not the favourite: 0.
		a.civilization.favoriteGovernmentName = "Monarchy";
		Assert.Equal(-10, Score(a, b, gd));

		// Different government, and it is the shunned one: +5.
		a.civilization.favoriteGovernmentName = "Monarchy";
		b.government = new Government { name = "Republic" };
		Assert.Equal(-5, Score(a, b, gd));

		// Different government, not shunned: +1.
		b.government = new Government { name = "Communism" };
		Assert.Equal(-9, Score(a, b, gd));
	}

	// The third-party sum already excludes the observer (its own record is the
	// score's own section 3.1 term, and counting it again here would double it)
	// and the subject (a civ's opinion of itself is meaningless).
	[Fact]
	public void TheThirdPartyLoopExcludesTheObserverAndTheSubject() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Player c = NewPlayer(gd, "C", 0, 2);
		gd.players.Add(c);
		a.EnsureRelationshipExists(c);
		b.EnsureRelationshipExists(c);

		// A's own record toward B is the section 3.1 term: +4 x 5 = +20. C's own
		// record toward B is worth min(10, 3) = 3 through the third-party sum.
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 5;
		Relationship(c, b).reputation.trustCounter = 10;

		// 20 - 10 (peace) + 3. If the observer were summed with itself as a third
		// party this would be 15 instead.
		Assert.Equal(20 - 10 + 3, Score(a, b, gd));
	}

	// The strength-halving gate is NOT the third-party loop's exclusion; it only
	// gates the halving. Suppressing the halving therefore leaves a third civ's
	// contribution to the sum intact.
	[Fact]
	public void SuppressingTheHalvingDoesNotRemoveACivFromTheThirdPartySum() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Player c = NewPlayer(gd, "C", 0, 2);
		gd.players.Add(c);
		a.EnsureRelationshipExists(c);
		b.EnsureRelationshipExists(c);
		Relationship(c, b).reputation.trustCounter = 10;

		Assert.Equal(-10 + 3, Score(a, b, gd));
		Assert.Equal(-10 + 3, Score(a, b, gd, suppressStrengthHalving: true));
	}

	// ------------------------------------------------------------------
	// Third-party opinion (spec section 3.4)
	// ------------------------------------------------------------------

	[Fact]
	public void ThirdPartyOpinionAddsTheirClampedViewOfTheSubject() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Player c = NewPlayer(gd, "C", 0, 2);
		gd.players.Add(c);
		a.EnsureRelationshipExists(c);
		b.EnsureRelationshipExists(c);

		DiplomaticReputation ofB = Relationship(c, b).reputation;
		ofB.warDeclarationsAgainstUs = 5;   // min(5, 2) = 2
		ofB.cancelledDeals = 5;             // min(5, 2) = 2
		ofB.trustCounter = 10;              // min(10, 3) = 3
		ofB.aggressionObserved = 5;         // min(5, 1) = 1
		ofB.field3C1 = 20;                  // min(20, 8) = 8
		ofB.field3C2 = 5;                   // min(5, 1) = 1

		Assert.Equal(-10 + 17, Score(a, b, gd));
	}

	// ------------------------------------------------------------------
	// The shared-enemy loop (the original's *first* loop, spec section 3.4a)
	// ------------------------------------------------------------------

	// A third civ that is at war with both A and B, with the four war-memory
	// counters of its own record toward B set to the given values.
	private static Player SharedEnemy(C7GameData.GameData gd, Player a, Player b,
			int caughtSpies, int field14, int warDamageMemory, int warDamageThisTurn) {
		Player p = NewPlayer(gd, "P", 0, 2);
		gd.players.Add(p);
		a.EnsureRelationshipExists(p);
		b.EnsureRelationshipExists(p);
		DeclareWar(a, p, sneakAttack: false, refuseContactUntilTurn: 10);
		DeclareWar(b, p, sneakAttack: false, refuseContactUntilTurn: 10);

		DiplomaticReputation ofB = Relationship(p, b).reputation;
		// The declaration above recorded a war declaration in P's own memory of
		// B; clear it so only the counters under test are non-zero.
		ofB.warDeclarationsAgainstUs = 0;
		ofB.caughtSpies = caughtSpies;
		ofB.field14 = field14;
		ofB.warDamageMemory = warDamageMemory;
		ofB.warDamageThisTurn = warDamageThisTurn;
		return p;
	}

	// Sharing an enemy makes A *less* hostile to B. The contribution is 3 plus
	// four counters of P's own record toward B - caught spies capped at 2,
	// field_14 at 4, war-damage memory at 5 and this turn's war damage at 1 -
	// so a single shared enemy can move 15 points, a full mood band on a
	// 100x100 map. Before the shared-enemy loop was modelled this case scored +6
	// (ANGRY); it must now score -9 (POLITE).
	[Fact]
	public void SharedEnemyLowersHostilityByTheClampedWarMemoryAndMovesAMoodBand() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		// +4 x 4 remembered war declarations, minus the -10 peace term: +6.
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 4;
		Assert.Equal(6, Score(a, b, gd));
		Assert.Equal(DiplomaticMood.Angry, DiplomaticAttitude.MoodToward(a, b, gd, suppressStrengthHalving: false));

		SharedEnemy(gd, a, b, caughtSpies: 5, field14: 9, warDamageMemory: 9, warDamageThisTurn: 9);

		// 3 + min(5,2) + min(9,4) + min(9,5) + min(9,1) = 15.
		Assert.Equal(6 - 15, Score(a, b, gd));
		Assert.Equal(DiplomaticMood.Polite, DiplomaticAttitude.MoodToward(a, b, gd, suppressStrengthHalving: false));
	}

	// Each of the four counters keeps its own cap: with every counter at 1 the
	// contribution is the flat 3 plus four single points, not 3 plus the caps.
	[Fact]
	public void SharedEnemyCountersUseTheirOwnCaps() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 4;

		SharedEnemy(gd, a, b, caughtSpies: 1, field14: 1, warDamageMemory: 1, warDamageThisTurn: 1);

		Assert.Equal(6 - 7, Score(a, b, gd));
	}

	// When B is NOT at war with the shared enemy, the counter branch cannot fire
	// and B's own trade embargo against P is worth -2 instead.
	[Fact]
	public void SubjectEmbargoingTheSharedEnemyIsWorthTwoInstead() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 4;

		Player p = NewPlayer(gd, "P", 0, 2);
		gd.players.Add(p);
		a.EnsureRelationshipExists(p);
		b.EnsureRelationshipExists(p);
		DeclareWar(a, p, sneakAttack: false, refuseContactUntilTurn: 10);
		// B embargoes P but stays at peace with them.
		RegisterMultiTurnDeal(b, p, new MultiTurnDeal(DealType.Embargo, DealSubType.TradeEmbargo,
			DealDetails.Exchange, 0, null, 20, 0, p.id));
		DiplomaticReputation ofB = Relationship(p, b).reputation;
		ofB.caughtSpies = 9;
		ofB.field14 = 9;
		ofB.warDamageMemory = 9;
		ofB.warDamageThisTurn = 9;

		Assert.False(AtWar(b, p));
		Assert.Equal(6 - 2, Score(a, b, gd));
	}

	// The original's first loop has TWO guards: the live-player mask
	// (`p_player_bits` @ `0xa526c0`, tested at `0x440482`) and A's at-war byte
	// (`A+0xD30+p`, `0x440490`). The engine has no bits mask, but its AtWar()
	// refuses a defeated player (PlayerRelationship.TryGetRelationship), so a
	// defeated civ is skipped exactly as a cleared live bit would skip it. This
	// pins that equivalence: without it the defeated civ would still contribute
	// its full 15-point war memory.
	[Fact]
	public void DefeatedCivsAreSkippedByTheSharedEnemyLoopLikeAClearedLiveBit() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 4;

		Player p = SharedEnemy(gd, a, b, caughtSpies: 5, field14: 9, warDamageMemory: 9, warDamageThisTurn: 9);
		Assert.Equal(6 - 15, Score(a, b, gd));

		// The relationships still exist and A is still nominally at war with p,
		// but p is out of the game, so the loop skips it.
		p.defeated = true;
		Assert.Equal(6, Score(a, b, gd));
	}

	// The loop only visits civs the OBSERVER is at war with. B being at war with
	// P is not enough, and adding A to that war changes the score by the full
	// clamp, which is what makes this test double-sided.
	[Fact]
	public void TheSharedEnemyLoopNeedsWarBetweenTheObserverAndTheEnemy() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 4;

		Player p = NewPlayer(gd, "P", 0, 2);
		gd.players.Add(p);
		a.EnsureRelationshipExists(p);
		b.EnsureRelationshipExists(p);
		DeclareWar(b, p, sneakAttack: false, refuseContactUntilTurn: 10);
		DiplomaticReputation ofB = Relationship(p, b).reputation;
		ofB.warDeclarationsAgainstUs = 0;
		ofB.caughtSpies = 9;
		ofB.field14 = 9;
		ofB.warDamageMemory = 9;
		ofB.warDamageThisTurn = 9;

		// A is at peace with P, so P is ignored by the shared-enemy loop.
		Assert.Equal(6, Score(a, b, gd));

		// A joins the war against P: the same P state now costs A 15 points.
		DeclareWar(a, p, sneakAttack: false, refuseContactUntilTurn: 10);
		Assert.Equal(6 - 15, Score(a, b, gd));
	}

	// The original's loop starts at civ index 1, so the barbarian slot can never
	// contribute however many enemies are shared with it.
	[Fact]
	public void TheSharedEnemyLoopSkipsTheBarbarianSlot() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 4;

		Civilization barbarianCiv = new Civilization("Barbarians") { isBarbarian = true };
		Player barbs = new Player {
			id = gd.ids.CreateID("player"),
			civilization = barbarianCiv,
			government = new Government { name = "Despotism" },
		};
		gd.players.Add(barbs);

		// Everyone is at war with the barbarians, so a loop without the skip
		// would subtract the flat 3 here.
		Assert.True(AtWar(a, barbs));
		Assert.True(AtWar(b, barbs));
		Assert.Equal(6, Score(a, b, gd));
	}

	// ------------------------------------------------------------------
	// The contact grievance bits (spec section 3.3)
	// ------------------------------------------------------------------

	// `A.Contacts[B]` bit 0x10 is worth the same +5 the at-war branch gives, and
	// bit 0x8 is worth +1 only when neither that bit nor the war bit is set.
	[Fact]
	public void ContactGrievanceBit16IsWorthFiveAndBit8IsWorthOne() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Assert.Equal(-10, Score(a, b, gd));

		Relationship(a, b).contactGrievances = PlayerRelationship.ContactGrievanceBit8;
		Assert.Equal(-9, Score(a, b, gd));

		Relationship(a, b).contactGrievances = PlayerRelationship.ContactGrievanceBit16;
		Assert.Equal(-5, Score(a, b, gd));

		// Both bits: 0x10 takes the +5 branch, so 0x8 does not add its +1.
		Relationship(a, b).contactGrievances =
			PlayerRelationship.ContactGrievanceBit8 | PlayerRelationship.ContactGrievanceBit16;
		Assert.Equal(-5, Score(a, b, gd));
	}

	// Being at war and grievance bit 0x10 share one branch, so a pair that is at
	// war and has the bit set gains +5 once, not +10, and the +1 of bit 0x8
	// never applies while the war is on.
	[Fact]
	public void TheWarBranchAndTheGrievanceBitShareTheSameFivePoints() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		DeclareWar(a, b, sneakAttack: false, refuseContactUntilTurn: 10);
		Assert.Equal(5, Score(a, b, gd));

		Relationship(a, b).contactGrievances =
			PlayerRelationship.ContactGrievanceBit8 | PlayerRelationship.ContactGrievanceBit16;
		Assert.Equal(5, Score(a, b, gd));
	}

	// Only the two grievance bits of an imported contact word reach the attitude
	// model; "met" (bit 1), "mutual" (bit 2) and "plotting war" (0x20) have
	// their own engine-side state and must not leak in as extra hostility.
	[Fact]
	public void OnlyTheTwoGrievanceBitsSurviveTheSavedContactWord() {
		Assert.Equal(0x18,
			PlayerRelationship.GrievanceBitsOfContactWord(0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40));
		Assert.Equal(0x8, PlayerRelationship.GrievanceBitsOfContactWord(0x3 | 0x8));
		Assert.Equal(0, PlayerRelationship.GrievanceBitsOfContactWord(0x1 | 0x2 | 0x20 | 0x40));
	}

	// ------------------------------------------------------------------
	// The two special cases and the halving rule (spec section 3.5)
	// ------------------------------------------------------------------

	// At war an observer cannot be friendly: a negative score is floored to 0.
	// The original does this before the clamp, so a war-time score is never
	// below zero.
	[Fact]
	public void NegativeHostilityIsFlooredAtZeroDuringWar() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		DeclareWar(a, b, sneakAttack: false, refuseContactUntilTurn: 10);
		Relationship(a, b).reputation.observedMilitaryAggression = 20;

		// -20 reputation + 5 war term = -15, floored to 0.
		Assert.Equal(0, Score(a, b, gd));
		Assert.Equal(DiplomaticMood.Cautious, DiplomaticAttitude.MoodToward(a, b, gd, suppressStrengthHalving: false));
	}

	// The direct callers push 0 for the gate - `0x44c95e`, `0x5186d2` and
	// `0x51b518` - as do the indirect Leader-vtable callers at `0x4384da` and
	// `0x447957`/`0x447972`, so a negative score is halved when the subject is the
	// stronger civ. The indirect caller at `0x441bef` pushes 1 and suppresses the
	// halving; that non-zero gate is what `suppressStrengthHalving: true` models.
	// Suppression does not change the third-party sum, a separate test.
	[Fact]
	public void FriendlinessIsHalvedWhenTheSubjectIsStrongerAndTheGateIsZero() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Player c = NewPlayer(gd, "C", 0, 2);
		gd.players.Add(c);

		Relationship(a, b).reputation.observedMilitaryAggression = 20;
		gd.history = new() {
			[a.id.ToString()] = new List<HistTurnRecord> { new() { Power = 100 } },
			[b.id.ToString()] = new List<HistTurnRecord> { new() { Power = 500 } },
		};

		// -20 - 10 = -30, halved to -15 because B is the stronger civ and the
		// gate is zero; a non-zero gate leaves it at -30.
		Assert.Equal(-15, Score(a, b, gd));
		Assert.Equal(-30, Score(a, b, gd, suppressStrengthHalving: true));

		// With equal power there is no halving.
		gd.history[b.id.ToString()][0].Power = 100;
		Assert.Equal(-30, Score(a, b, gd));
	}

	// The suppressed branch is the indirect path: the Leader-vtable caller at
	// `0x441bef` pushes 1 for the gate. The original only tests the byte for
	// zero, so that 1 and the civ index `0x535380` forwards are the same branch.
	// This pins that a suppressing caller is NOT halved, against the halved value
	// in the test above.
	[Fact]
	public void SuppressingTheStrengthHalvingLeavesANegativeScoreUnhalved() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.observedMilitaryAggression = 10;
		gd.history = new() {
			[a.id.ToString()] = new List<HistTurnRecord> { new() { Power = 100 } },
			[b.id.ToString()] = new List<HistTurnRecord> { new() { Power = 500 } },
		};

		// -10 - 10 = -20. The zero gate halves it to -10; the non-zero gate does
		// not.
		Assert.Equal(-10, Score(a, b, gd, suppressStrengthHalving: false));
		Assert.Equal(-20, Score(a, b, gd, suppressStrengthHalving: true));
	}

	// The mood function relays its own second stack argument into the same gate
	// (`0x440ade`), so a mood query can suppress the halving too: the mood caller
	// at `0x441b72` pushes 1 and `0x535380` forwards a civ index. The bands differ
	// here, so the relay is visible in the mood and not only in the score.
	[Fact]
	public void MoodTowardForwardsTheSuppressionToTheScore() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.observedMilitaryAggression = 10;
		gd.history = new() {
			[a.id.ToString()] = new List<HistTurnRecord> { new() { Power = 100 } },
			[b.id.ToString()] = new List<HistTurnRecord> { new() { Power = 500 } },
		};

		// The zero gate halves -20 to -10, which is POLITE on a 100x100 map; the
		// non-zero gate leaves -20, which is GRACIOUS.
		Assert.Equal(DiplomaticMood.Polite,
			DiplomaticAttitude.MoodToward(a, b, gd, suppressStrengthHalving: false));
		Assert.Equal(DiplomaticMood.Gracious,
			DiplomaticAttitude.MoodToward(a, b, gd, suppressStrengthHalving: true));
	}

	// UNVERIFIED PROXY (spec section 11 item 12): the halving compares the engine's
	// histograph `Power` value because it has no `power_rank`. This pins which
	// entry of the history is read - the LAST one - so switching to the maximum,
	// the first, or an average is a visible change.
	[Fact]
	public void TheStrengthHalvingReadsTheLastHistographPowerEntry() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.observedMilitaryAggression = 20;

		gd.history = new() {
			[a.id.ToString()] = new List<HistTurnRecord> { new() { Power = 900 }, new() { Power = 100 } },
			[b.id.ToString()] = new List<HistTurnRecord> { new() { Power = 500 }, new() { Power = 100 } },
		};

		// The last entries agree (100/100), so the score is not halved even
		// though A's maximum is higher and B's earlier entry is higher.
		Assert.Equal(-30, Score(a, b, gd));

		gd.history[b.id.ToString()][1].Power = 101;
		Assert.Equal(-15, Score(a, b, gd));
	}

	// UNVERIFIED PROXY (spec section 11 item 12): with no history at all a civ reads as
	// 100, the same stand-in Player.cs uses for the power value.
	[Fact]
	public void TheStrengthHalvingFallsBackTo100WithoutHistory() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Relationship(a, b).reputation.observedMilitaryAggression = 20;

		// Neither side has a history entry: 100 against 100, so no halving.
		Assert.Equal(-30, Score(a, b, gd));

		// B below the fallback is the weaker civ, so still no halving.
		gd.history = new() { [b.id.ToString()] = new List<HistTurnRecord> { new() { Power = 50 } } };
		Assert.Equal(-30, Score(a, b, gd));

		// B just above the fallback is the stronger civ, so the score halves.
		gd.history[b.id.ToString()][0].Power = 101;
		Assert.Equal(-15, Score(a, b, gd));
	}

	// UNVERIFIED PROXY (spec section 11 item 13): the -5 term keys off
	// `resourcesInBorders` (the tiles owned by the subject's cities, refreshed
	// only by the tile-owner pass), not off the original's own Available_Resources
	// table. This pins the current reading in all three states.
	[Fact]
	public void TheResourceTermReadsTheSubjectsResourcesInBorders() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Assert.Equal(-10, Score(a, b, gd));

		b.resourcesInBorders = new() { [new Resource()] = new List<Tile>() };
		Assert.Equal(-15, Score(a, b, gd));

		b.resourcesInBorders = new();
		Assert.Equal(-10, Score(a, b, gd));

		b.resourcesInBorders = null;
		Assert.Equal(-10, Score(a, b, gd));
	}

	// ------------------------------------------------------------------
	// The seed transform (spec section 3.5)
	// ------------------------------------------------------------------

	// `Race_get_effective_aggression_level` @ 0x53a0b0: the race's own
	// AggressionLevel plus half the game difficulty index minus a sixth of the
	// rules' difficulty-level count, then the five-way AI Aggression band, then a
	// clamp to -2..2. Every branch of the jump table is asserted exactly.
	[Fact]
	public void TheSeedTransformMatchesRaceGetEffectiveAggressionLevel() {
		// Neutral difficulty and no rules count: the race's own level, already in
		// range, passes through.
		Assert.Equal(0, DiplomaticAttitude.EffectiveAggressionSeed(0, 0, 0));
		Assert.Equal(2, DiplomaticAttitude.EffectiveAggressionSeed(2, 0, 0));
		Assert.Equal(-2, DiplomaticAttitude.EffectiveAggressionSeed(-2, 0, 0));

		// + trunc(difficulty / 2), - trunc(difficultyLevelCount / 6).
		Assert.Equal(2, DiplomaticAttitude.EffectiveAggressionSeed(2, 3, 0));
		Assert.Equal(1, DiplomaticAttitude.EffectiveAggressionSeed(1, 3, 8));
		Assert.Equal(2, DiplomaticAttitude.EffectiveAggressionSeed(1, 4, 8));
		Assert.Equal(2, DiplomaticAttitude.EffectiveAggressionSeed(2, 6, 0));
		Assert.Equal(-1, DiplomaticAttitude.EffectiveAggressionSeed(-1, 2, 8));

		// The clamp, after the difficulty shift.
		Assert.Equal(-2, DiplomaticAttitude.EffectiveAggressionSeed(-2, 0, 8));
		Assert.Equal(2, DiplomaticAttitude.EffectiveAggressionSeed(2, 7, 0));

		// The AI Aggression bands: 0 and 4 replace the value outright, 1 and 3
		// shift it and then clamp, 2 and anything above 4 leave it alone.
		Assert.Equal(-2, DiplomaticAttitude.EffectiveAggressionSeed(2, 6, 0, 0));
		Assert.Equal(2, DiplomaticAttitude.EffectiveAggressionSeed(-2, 0, 0, 4));
		Assert.Equal(-1, DiplomaticAttitude.EffectiveAggressionSeed(0, 0, 0, 1));
		Assert.Equal(1, DiplomaticAttitude.EffectiveAggressionSeed(0, 0, 0, 3));
		Assert.Equal(-2, DiplomaticAttitude.EffectiveAggressionSeed(-2, 0, 0, 1));
		Assert.Equal(2, DiplomaticAttitude.EffectiveAggressionSeed(2, 0, 0, 3));
		Assert.Equal(0, DiplomaticAttitude.EffectiveAggressionSeed(0, 0, 0, 2));
		Assert.Equal(2, DiplomaticAttitude.EffectiveAggressionSeed(2, 0, 0, 5));

		// The engine has no AI Aggression setting, so the default is Normal (2).
		Assert.Equal(DiplomaticAttitude.DefaultAiAggressionSetting, 2);
		Assert.Equal(DiplomaticAttitude.EffectiveAggressionSeed(1, 0, 0),
			DiplomaticAttitude.EffectiveAggressionSeed(1, 0, 0, DiplomaticAttitude.DefaultAiAggressionSetting));
	}

	// The seed reads the game's difficulty and the rules' difficulty list, so a
	// mid-list difficulty on a full ruleset shifts the score by +1 - unlike a
	// bare test game, whose empty difficulty list and absent game difficulty read
	// as index 0 of 0.
	[Fact]
	public void TheScoreSeedReadsTheGameDifficultyAndTheRulesDifficultyList() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Assert.Empty(gd.difficulties);
		Assert.Equal(-10, Score(a, b, gd));

		for (int i = 0; i < 8; i++) {
			gd.difficulties.Add(new Difficulty { id = gd.ids.CreateID("difficulty"), Name = $"D{i}" });
		}

		// The fifth difficulty of eight: trunc(4/2) - trunc(8/6) = +1.
		gd.gameDifficulty = gd.difficulties[4];
		Assert.Equal(1 - 10, Score(a, b, gd));

		// The first difficulty of eight: trunc(0/2) - trunc(8/6) = -1.
		gd.gameDifficulty = gd.difficulties[0];
		Assert.Equal(-1 - 10, Score(a, b, gd));

		// A difficulty object that is not in the list reads as index 0, so the
		// shift is the rules-count term alone.
		gd.gameDifficulty = new Difficulty();
		Assert.Equal(-1 - 10, Score(a, b, gd));
	}

	// ------------------------------------------------------------------
	// Mood thresholds (spec section 4)
	// ------------------------------------------------------------------

	// On a 100x100 map both thresholds are 10, so the bands are
	// H <= -11 GRACIOUS, -10..-1 POLITE, 0 CAUTIOUS, 1..10 ANGRY, >= 11 FURIOUS.
	// Every boundary value is asserted exactly.
	[Fact]
	public void MoodBoundariesOnA100x100MapAreMinus11And11() {
		Assert.Equal(DiplomaticMood.Gracious, DiplomaticAttitude.MoodForScore(-12, 100, 100));
		Assert.Equal(DiplomaticMood.Gracious, DiplomaticAttitude.MoodForScore(-11, 100, 100));
		Assert.Equal(DiplomaticMood.Polite, DiplomaticAttitude.MoodForScore(-10, 100, 100));
		Assert.Equal(DiplomaticMood.Polite, DiplomaticAttitude.MoodForScore(-1, 100, 100));
		Assert.Equal(DiplomaticMood.Cautious, DiplomaticAttitude.MoodForScore(0, 100, 100));
		Assert.Equal(DiplomaticMood.Angry, DiplomaticAttitude.MoodForScore(1, 100, 100));
		Assert.Equal(DiplomaticMood.Angry, DiplomaticAttitude.MoodForScore(10, 100, 100));
		Assert.Equal(DiplomaticMood.Furious, DiplomaticAttitude.MoodForScore(11, 100, 100));
	}

	// Both thresholds scale with the map: on a 99x100 map the perimeter sum is
	// 199, half is 99 and floor(199/20) is 9, so the bands are 9 wide, not 10.
	[Fact]
	public void MoodThresholdsScaleWithMapSize() {
		Assert.Equal(DiplomaticMood.Gracious, DiplomaticAttitude.MoodForScore(-10, 99, 100));
		Assert.Equal(DiplomaticMood.Polite, DiplomaticAttitude.MoodForScore(-9, 99, 100));
		Assert.Equal(DiplomaticMood.Angry, DiplomaticAttitude.MoodForScore(9, 99, 100));
		Assert.Equal(DiplomaticMood.Furious, DiplomaticAttitude.MoodForScore(10, 99, 100));
	}

	// ------------------------------------------------------------------
	// The per-turn drift (spec 10_turn_sequence.md section 3 step 4)
	// ------------------------------------------------------------------

	// The drift is per counter with its own probability, and it is signed
	// downward only. A rng that always returns 0 makes every roll succeed.
	[Fact]
	public void DecayOneTurnTicksEveryCounterAtItsOwnRate() {
		DiplomaticReputation rep = new DiplomaticReputation {
			warDeclarationsAgainstUs = 5,
			cancelledDeals = 5,
			trustCounter = 5,
			aggressionObserved = 5,
			caughtSpies = 5,
			field14 = 5,
			tribute = 5,
			observedMilitaryAggression = 5,
			diplomaticThreats = 5,
			gifts = 25,
			refusedTributeDemands = 5,
			icbm = 5,
			icbmOther = 5,
			warDamageMemory = 5,
			warDamageThisTurn = 5,
			field3C0 = 5,
			field3C1 = 5,
			field3C2 = 5,
			field3C3 = 5,
		};

		rep.DecayOneTurn(_ => 0);

		Assert.Equal(new[] { 4, 4, 4, 4, 4, 4, 4, 4, 4, 15, 4, 4, 4, 4, 0, 4, 4, 4, 4 }, new[] {
			rep.warDeclarationsAgainstUs, rep.cancelledDeals, rep.trustCounter,
			rep.aggressionObserved, rep.caughtSpies, rep.field14, rep.tribute,
			rep.observedMilitaryAggression, rep.diplomaticThreats, rep.gifts,
			rep.refusedTributeDemands, rep.icbm, rep.icbmOther, rep.warDamageMemory,
			rep.warDamageThisTurn, rep.field3C0, rep.field3C1, rep.field3C2, rep.field3C3,
		});
	}

	// A roll that never hits leaves the counters alone and never raises them.
	[Fact]
	public void DecayOneTurnNeverRaisesACounterAndNeverGoesBelowZero() {
		DiplomaticReputation untouched = new DiplomaticReputation {
			warDeclarationsAgainstUs = 3,
			caughtSpies = 3,
			field14 = 3,
			icbm = 3,
			warDamageThisTurn = 3,
		};
		untouched.DecayOneTurn(_ => 1);
		Assert.Equal(3, untouched.warDeclarationsAgainstUs);
		Assert.Equal(3, untouched.caughtSpies);
		Assert.Equal(3, untouched.field14);
		Assert.Equal(3, untouched.icbm);
		// The reset-to-zero entry is unconditional, so it still fires.
		Assert.Equal(0, untouched.warDamageThisTurn);

		DiplomaticReputation zeroed = new DiplomaticReputation();
		zeroed.DecayOneTurn(_ => 0);
		Assert.Equal(0, zeroed.warDeclarationsAgainstUs);
		Assert.Equal(0, zeroed.caughtSpies);
		Assert.Equal(0, zeroed.warDamageMemory);
	}

	// The drift is what moves the *score* toward its resting value over time:
	// a war memory decays away and the pair relaxes back toward the peace term.
	[Fact]
	public void ReputationDriftLowersTheScoreOverSeveralTurns() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		// Two remembered war declarations: +8 on the score.
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 2;
		Assert.Equal(-2, Score(a, b, gd));
		Assert.Equal(DiplomaticMood.Polite, DiplomaticAttitude.MoodToward(a, b, gd, suppressStrengthHalving: false));

		for (int turn = 0; turn < 2; turn++) {
			DecayReputationMemories(a, gd, _ => 0);
		}

		Assert.Equal(0, Relationship(a, b).reputation.warDeclarationsAgainstUs);
		Assert.Equal(-10, Score(a, b, gd));
	}

	// The drift runs for the observing civ only, skips barbarians, and skips
	// civs that have been eliminated.
	[Fact]
	public void DecayReputationMemoriesCoversLivingOthersOnly() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();
		Player c = NewPlayer(gd, "C", 0, 2);
		gd.players.Add(c);
		a.EnsureRelationshipExists(c);

		Relationship(a, b).reputation.warDeclarationsAgainstUs = 5;
		Relationship(a, c).reputation.warDeclarationsAgainstUs = 5;
		Relationship(b, a).reputation.warDeclarationsAgainstUs = 5;

		c.defeated = true;
		DecayReputationMemories(a, gd, _ => 0);

		Assert.Equal(4, Relationship(a, b).reputation.warDeclarationsAgainstUs);
		Assert.Equal(5, Relationship(a, c).reputation.warDeclarationsAgainstUs);
		// B is not the observer, so B's own memory is untouched.
		Assert.Equal(5, Relationship(b, a).reputation.warDeclarationsAgainstUs);

		// Barbarians have no memory (the original returns immediately for
		// player index 0).
		a.civilization.isBarbarian = true;
		Relationship(a, b).reputation.warDeclarationsAgainstUs = 5;
		DecayReputationMemories(a, gd, _ => 0);
		Assert.Equal(5, Relationship(a, b).reputation.warDeclarationsAgainstUs);
	}

	// ------------------------------------------------------------------
	// Events (spec section 9.2)
	// ------------------------------------------------------------------

	// A war declaration is remembered by the DEFENDER's record as index 0, and
	// the score of the defender toward the aggressor goes up.
	[Fact]
	public void WarDeclarationRaisesTheDefendersHostility() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		Assert.Equal(-10, Score(b, a, gd));
		DeclareWar(a, b, sneakAttack: false, refuseContactUntilTurn: 10);

		Assert.Equal(1, Relationship(b, a).reputation.warDeclarationsAgainstUs);
		Assert.Equal(0, Relationship(a, b).reputation.warDeclarationsAgainstUs);
		// +4 for the declaration, -10 peace term replaced by +5 war term.
		Assert.Equal(9, Score(b, a, gd));
	}

	// A sneak attack additionally records one observation of aggression
	// (index 3) on the defender's side.
	[Fact]
	public void SneakAttackRecordsObservedAggression() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		DeclareWar(a, b, sneakAttack: true, refuseContactUntilTurn: 10);

		Assert.Equal(1, Relationship(b, a).reputation.aggressionObserved);
		Assert.Equal(1, Relationship(b, a).reputation.warDeclarationsAgainstUs);
		// +4 declaration, +2 aggression, +5 war term.
		Assert.Equal(11, Score(b, a, gd));
	}

	// A caught spy is remembered by the civ that caught it (index 4).
	[Fact]
	public void CaughtSpyRaisesTheCatchersHostility() {
		(C7GameData.GameData gd, Player actor, Player target, City targetCity) = EspionagePair();
		C7GameData.GameData.rng = new FixedRandom(99);

		EspionageMissionResult result = Espionage.RunMission(gd, actor, target, targetCity,
			Espionage.PlantSpy, EspionageAgent.Spy);

		Assert.True(result.agentCaught);
		Assert.Equal(1, Relationship(target, actor).reputation.caughtSpies);
		Assert.Equal(0, Relationship(actor, target).reputation.caughtSpies);
		// The +4 spy term replaces the -10 peace term, but an AI target that
		// catches a spy also declares war (Espionage.NotifySpyCaught), which
		// swaps in the +5 war term and shows up as a war declaration in the
		// actor's own record.
		Assert.Equal(9, Score(target, actor, gd));
		Assert.Equal(1, Relationship(actor, target).reputation.warDeclarationsAgainstUs);
	}

	// A completed technology deal bumps the trade memory (index 10) on BOTH
	// sides, and a deal without a technology leaves it alone.
	[Fact]
	public void CompletedTechnologyDealIsRememberedByBothSides() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		a.ExecuteDeal(gd, b, new TradeOffer(), new TradeOffer());
		Assert.Equal(0, Relationship(a, b).reputation.refusedTributeDemands);
		Assert.Equal(0, Relationship(b, a).reputation.refusedTributeDemands);

		Tech tech = new() { id = gd.ids.CreateID("tech"), Name = "Pottery" };
		gd.techs.Add(tech);
		TradeOffer withTech = new TradeOffer();
		withTech.techs.Add(tech);

		a.ExecuteDeal(gd, b, new TradeOffer(), withTech);

		Assert.Equal(1, Relationship(a, b).reputation.refusedTributeDemands);
		Assert.Equal(1, Relationship(b, a).reputation.refusedTributeDemands);
	}

	// A trespass / demand-withdrawal event is remembered as index 18, a
	// +1-per-count term.
	[Fact]
	public void TrespassIsRememberedAsField3C3() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		Relationship(a, b).reputation.RecordTrespass();
		Assert.Equal(1, Relationship(a, b).reputation.field3C3);
		Assert.Equal(-9, Score(a, b, gd));
	}

	// Peace halves the war-damage memory and forgets this turn's war damage on
	// both sides.
	[Fact]
	public void PeaceHalvesWarDamageMemoryAndClearsThisTurnsWarDamage() {
		(C7GameData.GameData gd, Player a, Player b) = NewPair();

		DeclareWar(a, b, sneakAttack: false, refuseContactUntilTurn: 10);
		foreach (PlayerRelationship rel in new[] { Relationship(a, b), Relationship(b, a) }) {
			rel.reputation.warDamageMemory = 21;
			rel.reputation.warDamageThisTurn = 7;
		}

		SignPeaceAfterWar(a, b, gd);

		Assert.Equal(10, Relationship(a, b).reputation.warDamageMemory);
		Assert.Equal(0, Relationship(a, b).reputation.warDamageThisTurn);
		Assert.Equal(10, Relationship(b, a).reputation.warDamageMemory);
		Assert.Equal(0, Relationship(b, a).reputation.warDamageThisTurn);
	}

	// ------------------------------------------------------------------
	// The two data paths (the mistake that has shipped three times): the
	// attitude model reads these values, so they must exist on the BIQ import
	// path AND in C7/Lua/civ3/ruleset.json.
	// ------------------------------------------------------------------

	[Fact]
	public void RulesetJsonCarriesTheAttitudeData() {
		string path = Path.Combine(PathUtils.GameModesDir, "civ3", "ruleset.json");
		SaveGame rules = SaveGame.LoadFromJSON(File.ReadAllText(path));

		Assert.Equal(32, rules.Civilizations.Count);
		// Every civilization the rules define must carry the seed and both
		// government preferences, or a ruleset game silently scores as if every
		// civ were aggression 0 with no favourite government.
		Assert.All(rules.Civilizations, civ => Assert.InRange(civ.aggressionLevel, -2, 2));
		Assert.All(rules.Civilizations.Skip(1), civ => {
			Assert.False(string.IsNullOrEmpty(civ.favoriteGovernmentName));
			Assert.False(string.IsNullOrEmpty(civ.shunnedGovernmentName));
		});

		// Spot-checks against the values measured out of conquests.biq.
		Assert.Equal(-2, rules.Civilizations.Single(c => c.name == "France").aggressionLevel);
		Assert.Equal(2, rules.Civilizations.Single(c => c.name == "Germany").aggressionLevel);
		Assert.Equal("Communism", rules.Civilizations.Single(c => c.name == "Rome").shunnedGovernmentName);
		Assert.Equal("Despotism", rules.Civilizations.Single(c => c.name == "Egypt").favoriteGovernmentName);
	}

	// The same values must reach Civilization objects on the BIQ import path.
	// A scenario with a map runs the whole ImportBiq path.
	[SkippableFact]
	public void BiqImportCarriesTheAttitudeData() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		string scenarioPath = Path.Combine(Civ3Location.GetCiv3Path(), "Conquests", "Conquests", "1 Mesopotamia.biq");
		EngineStorage.animationsEnabled = false;
		SaveGame save = ImportCiv3.ImportBiq(scenarioPath, PathUtils.defaultBicPath, GetPediaIconsPath("Conquests/Conquests"));
		BiqData biq = BiqData.LoadFile(scenarioPath);

		Assert.Equal(biq.Race.Length, save.Civilizations.Count);
		for (int i = 0; i < biq.Race.Length; i++) {
			RACE race = biq.Race[i];
			Civilization civ = save.Civilizations[i];
			Assert.Equal(race.Name, civ.name);
			Assert.Equal(race.AggressionLevel, civ.aggressionLevel);
			Assert.Equal(GovernmentNameOrNull(biq, race.FavoriteGovernment), civ.favoriteGovernmentName);
			Assert.Equal(GovernmentNameOrNull(biq, race.ShunnedGovernment), civ.shunnedGovernmentName);
		}
	}

	private static string GovernmentNameOrNull(BiqData biq, int governmentIndex) {
		if (governmentIndex < 0 || governmentIndex >= biq.Govt.Length)
			return null;
		return biq.Govt[governmentIndex].Name;
	}

	private static Func<string, string> GetPediaIconsPath(string subfolder) {
		return (relativeModPath) => {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				relativeModPath = relativeModPath.Replace("\\conquests\\", "/Conquests/");
			}
			return Path.GetFullPath(Path.Combine(
				Civ3Location.GetCiv3Path(), subfolder, relativeModPath, "Text", "PediaIcons.txt"));
		};
	}

	// The espionage setup the caught-spy test needs, mirroring the one the
	// espionage tests use.
	private static (C7GameData.GameData gd, Player actor, Player target, City targetCity) EspionagePair() {
		C7GameData.GameData gd = EspionageTestGame.NewGameData();
		Player actor = EspionageTestGame.NewPlayer(gd, "actor", 100000);
		Player target = EspionageTestGame.NewPlayer(gd, "target", 100000);
		EspionageTestGame.Meet(actor, target);

		Tile actorTile = EspionageTestGame.NewTile(gd, 0, 0);
		Tile targetTile = EspionageTestGame.NewTile(gd, 8, 8);
		City actorCity = EspionageTestGame.NewCity(gd, actor, actorTile, "Capital", 1, actor.civilization);
		actorCity.capital = true;
		EspionageTestGame.AddWriting(gd, actor);
		actorCity.AddBuilding(EspionageTestGame.NewIntelligenceAgency(gd));
		City targetCity = EspionageTestGame.NewCity(gd, target, targetTile, "Target", 6, target.civilization);
		return (gd, actor, target, targetCity);
	}
}
