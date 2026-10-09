using System;
using System.Linq;

namespace C7GameData;

/// <summary>
/// The five-level view of one civ's opinion of another: `AIDiploMood` in the
/// shipped engine, whose values are the branch order of
/// `Leader_get_diplomatic_mood_toward` @ `0x440ad0` (vtable slot 34).
/// The game's text list `#ANGER_AT_LEVELS` in `Text/diplomacy.txt` spells the
/// same five levels "Gracious, Polite, Cautious, Annoyed, Furious".
/// **[C]** for the order, **[L]** for the text mapping (spec 20_diplomacy_trade.md
/// section 4).
/// </summary>
public enum DiplomaticMood {
	Gracious = 0,
	Polite = 1,
	Cautious = 2,
	Angry = 3,
	Furious = 4,
}

/// <summary>
/// One civ's per-pair memory record: the 19-int `reputations[B]` block the
/// shipped leader record keeps at `A+0x1B0 + B*0x4C` and the attitude function
/// `Leader_get_attitude_toward` @ `0x440100` sums (spec 20_diplomacy_trade.md
/// section 3.1). Every counter is a non-negative accumulator; the per-turn
/// drift in <see cref="DecayOneTurn"/> is what relaxes them toward zero over
/// time, which is why a score can decay toward its resting value even when no
/// new event occurs. (Signing peace is the other writer that lowers a counter,
/// by halving the war-damage memory.)
///
/// The field names below are the external header's names where section 3.1
/// gave one and this project's descriptive labels otherwise; the *weights* are
/// read off the binary and are the contract (section 13 tags the names **[?]**).
/// </summary>
public class DiplomaticReputation {
	// Section 3.1 index 0. Incremented on the defending side by every war
	// declaration (Leader_declare_war @ 0x501f20 step 4). The attitude function
	// weights it +4 per count with NO cap, unlike its neighbours: the
	// disassembly at 0x440135 loads `[A+0x1B0+B*0x4C]` and scales it by four
	// without the `cmp 8` guard that the very next field (`+0x1B4`) has, so
	// spec section 3.1's "4 x min(rep, 2)" for this row is wrong.
	public int warDeclarationsAgainstUs;

	// Section 3.1 index 1, `field_4` in the header. +4 x min(rep, 2).
	public int cancelledDeals;

	// Section 3.1 index 2, the header's `field_8` "trust" counter. +8 x min(rep, 2).
	public int trustCounter;

	// Section 3.1 index 3, the header's `field_C` "aggression observed":
	// incremented by 0x501f20 when war is declared from inside the target's
	// territory or while allied (spec section 9.1 step 3). +2 x min(rep, 2).
	public int aggressionObserved;

	// Section 3.1 index 4, `caught_spy`: incremented by the provocation handler
	// 0x502cc0 when the other civ catches one of our spies. +4 x rep, no cap.
	public int caughtSpies;

	// Section 3.1 index 5, `field_14`. +min(rep, 10).
	public int field14;

	// Section 3.1 index 6, `tribute`. +2 x rep.
	public int tribute;

	// Section 3.1 index 7, `observed_military_aggression`; the header name is
	// doubtful (section 3.1's note) but the weight is certain: it is SUBTRACTED.
	public int observedMilitaryAggression;

	// Section 3.1 index 8, `diplomatic_threats`. +rep.
	public int diplomaticThreats;

	// Section 3.1 index 9, `gift`. -min(rep / 10, 10).
	public int gifts;

	// Section 3.1 index 10, `refused_tribute_demands` in the header; also
	// incremented once per completed technology deal on both sides. Its weight
	// is a flat -1 while the counter is positive.
	public int refusedTributeDemands;

	// Section 3.1 index 11, `icbm`. +32 x rep.
	public int icbm;

	// Section 3.1 index 12, `icbm_other`. +16 x rep.
	public int icbmOther;

	// Section 3.1 index 13, `war_damage_memory`: halved when peace is signed. +min(rep, 10).
	public int warDamageMemory;

	// Section 3.1 index 14, `war_damage_this_turn`: zeroed when peace is signed. +min(rep, 10).
	public int warDamageThisTurn;

	// Section 3.1 indices 15-18, the header's `field_3C[0..3]`. Weights
	// -min(rep, 10), +16 x rep, +rep, +rep. Index 18 is incremented by the
	// trespass/provocation handler FUN_00438370 @ 0x438370 (spec section 9.2).
	public int field3C0;
	public int field3C1;
	public int field3C2;
	public int field3C3;

	/// <summary>
	/// The defender's memory of a war declaration by the subject (reputation
	/// index 0, `field_0`). Leader_declare_war @ 0x501f20 step 4.
	/// </summary>
	public void RecordWarDeclaration() {
		warDeclarationsAgainstUs++;
	}

	/// <summary>
	/// One observation of aggression by the subject (index 3, `field_C`): a war
	/// declared from inside our territory or while we were allied.
	/// Leader_declare_war @ 0x501f20 step 3.
	/// </summary>
	public void RecordAggressionObserved() {
		aggressionObserved++;
	}

	/// <summary>
	/// We caught a spy of the subject (index 4, `caught_spy`). Incremented by the
	/// provocation handler FUN_00502cc0 @ 0x502cc0, and by the engine's
	/// espionage bookkeeping when a mission's agent is exposed.
	/// </summary>
	public void RecordSpyCaught() {
		caughtSpies++;
	}

	/// <summary>
	/// A technology deal with the subject completed (index 10, the header's
	/// `refused_tribute_demands`, which the trade code shares). The AI-to-AI
	/// trade loop @ 0x43e470 bumps it on both sides next to the
	/// "Tech traded!!!!" debug string.
	/// </summary>
	public void RecordCompletedTechnologyDeal() {
		refusedTributeDemands++;
	}

	/// <summary>
	/// A trespass / demand-withdrawal event involving the subject (index 18,
	/// `field_3C[3]`). The shipped handler FUN_00438370 @ 0x438370 increments it
	/// and raises human notification code 8.
	/// </summary>
	public void RecordTrespass() {
		field3C3++;
	}

	/// <summary>
	/// Peace was signed: the accumulated war-damage memory is halved and this
	/// turn's war damage is forgotten. Leader_make_peace @ 0x5025b0 step 2 runs
	/// this on both sides.
	/// </summary>
	public void HalveWarDamageOnPeace() {
		warDamageMemory /= 2;
		warDamageThisTurn = 0;
	}

	/// <summary>
	/// A record for a civ that has no relationship with the queried one: the
	/// shipped engine simply reads the zeroed block, so a query against an unmet
	/// civ still returns a real score.
	/// </summary>
	public static DiplomaticReputation Zero => new DiplomaticReputation();

	/// <summary>
	/// The weighted sum of the 19 reputation terms, i.e. the static part of the
	/// hostility score (spec 20_diplomacy_trade.md section 3.1). Positive values
	/// make the observer MORE hostile.
	/// </summary>
	public int HostilityContribution() {
		int score = 0;
		score += 4 * warDeclarationsAgainstUs;
		score += Math.Min(4 * cancelledDeals, 8);
		score += Math.Min(8 * trustCounter, 16);
		score += Math.Min(2 * aggressionObserved, 4);
		score += 4 * caughtSpies;
		score += Math.Min(field14, 10);
		score += 2 * tribute;
		score -= observedMilitaryAggression;
		score += diplomaticThreats;
		score -= Math.Min(gifts / 10, 10);
		score -= refusedTributeDemands > 0 ? 1 : 0;
		score += 32 * icbm;
		score += 16 * icbmOther;
		score += Math.Min(warDamageMemory, 10);
		score += Math.Min(warDamageThisTurn, 10);
		score -= Math.Min(field3C0, 10);
		score += 16 * field3C1;
		score += field3C2;
		score += field3C3;
		return score;
	}

	/// <summary>
	/// The "AI state decay" that `Leader_begin_turn` @ `0x446840` runs for every
	/// other living civ, before that civ's own turn is played: each counter with
	/// its own independent probability ticks down by one, one counter drops by a
	/// flat ten, and one is reset. The drift is one-directional (toward zero) and
	/// a counter already at zero never moves, so the reputation half of the
	/// hostility score relaxes toward a resting value while the non-reputation
	/// terms (seed, government, war/peace, treaties) stay put. **[C]** for the
	/// per-index probabilities and the flat/reset entries
	/// (10_turn_sequence.md section 3 step 4).
	/// </summary>
	/// <param name="randInt">
	/// `randInt(n)` must return a value in `[0, n)`; the shipped engine uses
	/// `rand_int`. Pass a deterministic function in tests.
	/// </param>
	public void DecayOneTurn(Func<int, int> randInt) {
		if (warDeclarationsAgainstUs > 0 && randInt(0x80) == 0) warDeclarationsAgainstUs--;
		if (cancelledDeals > 0 && randInt(0x100) == 0) cancelledDeals--;
		if (trustCounter > 0 && randInt(0x200) == 0) trustCounter--;
		if (aggressionObserved > 0 && randInt(0x200) == 0) aggressionObserved--;
		if (caughtSpies > 0 && randInt(0x20) == 0) caughtSpies--;
		if (field14 > 0 && randInt(4) == 0) field14--;
		if (tribute > 0 && randInt(0x40) == 0) tribute--;
		if (observedMilitaryAggression > 0 && randInt(0x20) == 0) observedMilitaryAggression--;
		if (diplomaticThreats > 0 && randInt(0x20) == 0) diplomaticThreats--;
		if (gifts > 10) gifts -= 10;
		if (refusedTributeDemands > 0 && randInt(0x10) == 0) refusedTributeDemands--;
		if (icbm > 0 && randInt(0x400) == 0) icbm--;
		if (icbmOther > 0 && randInt(0x100) == 0) icbmOther--;
		if (warDamageMemory > 0 && randInt(2) == 0) warDamageMemory--;
		if (warDamageThisTurn > 0) warDamageThisTurn = 0;
		if (field3C0 > 0 && randInt(0x400) == 0) field3C0--;
		if (field3C1 > 0 && randInt(0x400) == 0) field3C1--;
		if (field3C2 > 0 && randInt(0x80) == 0) field3C2--;
		if (field3C3 > 0 && randInt(0x20) == 0) field3C3--;
	}
}

/// <summary>
/// The diplomatic attitude model: the single hostility score of
/// `Leader_get_attitude_toward` @ `0x440100` (vtable slot 33) and the five-level
/// mood of `Leader_get_diplomatic_mood_toward` @ `0x440ad0` (vtable slot 34),
/// from spec 20_diplomacy_trade.md sections 3 and 4.
///
/// SIGN CONVENTION - the score is HOSTILITY, not friendliness: positive = the
/// observer dislikes the subject. Gifts, embassies, peace and MPPs lower it; war
/// declarations, a caught spy and deal-breaking raise it. An inverted scale here
/// would silently invert every AI decision built on it.
/// </summary>
public static class DiplomaticAttitude {
	/// <summary>
	/// A's hostility toward B, the sum of the reputation record, the comparative
	/// terms, the relation/contact flags and third-party opinion, then the
	/// at-war floor, the "we are weaker" halving and the map-size clamp.
	/// </summary>
	/// <param name="suppressStrengthHalving">
	/// The gate on the "we are weaker" halving, made explicit. The original reads
	/// its third argument (its second stack argument, the first being the Leader
	/// `this`) in exactly one place, `0x440673`, as a byte: when that byte is zero
	/// and the score is negative and the subject is the stronger civ, the score is
	/// halved; when it is non-zero the halving is skipped. This parameter is that
	/// byte's zero/non-zero test: `false` is a zero gate (halve), `true` is a
	/// non-zero gate (do not halve).
	///
	/// It is NOT the third-party loop's exclusion: that loop's guard compares the
	/// candidate against the subject itself (`0x44056f` tests the same stack slot
	/// whose value indexes `A.reputations` at `0x440135`), which is `other` below.
	///
	/// BOTH populations of callers matter, because the attitude function is a
	/// Leader vtable slot (slot `0x84` of the vtable at `0x66cb38`) as well as a
	/// directly called function, so a search for a direct `call 0x440100` finds
	/// only the first population (`PROGRESS.md` finding 48). The direct callers
	/// push 0 - `0x44c95e`, `0x5186d2` and `0x51b518` - as do the indirect ones at
	/// `0x4384da` and `0x447957`/`0x447972` (the per-turn `Leader_begin_turn`), so
	/// they pass `false`. The indirect caller at `0x441bef` pushes 1 and so passes
	/// `true`. The mood function (slot `0x88`, `0x440ad0`) relays its own second
	/// stack argument into the same gate at `0x440ade`, so its callers decide:
	/// `0x441b72` pushes 1, while the rest push 0. The mood caller at `0x535380`
	/// was first reported as forwarding a civ index and suppressing the halving;
	/// it in fact pushes a register that is cleared to zero before every path
	/// reaches it (`0x534df6` writes zero, and the only cross-references to
	/// `0x53534c` are `0x534e04` and `0x5350e9`, both after that), so it is a
	/// gate-0 site and the halving applies there (`PROGRESS.md` finding 53). The
	/// only genuinely suppressing sites are therefore `0x441bef` and `0x441b72`,
	/// both pushing 1.
	/// Spec 20 section 3.6 and the note under its open item 2 record both
	/// populations.
	///
	/// The argument's intended meaning in the original source is still unlabelled
	/// (spec 20 section 11 item 2); what is measured is only its zero/non-zero
	/// test, which is all this parameter models. The old model used the barbarian
	/// `Player` as a sentinel for "gate zero". That sentinel did work - a
	/// non-barbarian `Player` made the guard false and suppressed the halving, and
	/// a test predating this API asserted exactly that - but its default
	/// conflated "no exception supplied" with "halve", a silent default that a
	/// required parameter removes.
	/// </param>
	public static int HostilityScore(Player self, Player other, GameData gameData, bool suppressStrengthHalving) {
		if (self == null || other == null)
			throw new ArgumentNullException(nameof(self), "Hostility must be queried between two known players.");
		if (self.id == other.id)
			throw new ArgumentException("A civ has no attitude toward itself.");

		DiplomaticReputation rep = Reputation(self, other);
		PlayerRelationship selfRel = Relationship(self, other);
		bool met = selfRel != null;

		// Section 3.6 seed: the race vtable slot 8 query of A's race record, the
		// header's get_effective_aggression_level (`Race_get_effective_aggression_level`
		// @ 0x53a0b0). That body is modelled in full by
		// EffectiveAggressionSeed below; the engine cannot supply one of its four
		// inputs, so the default stands in for the shipped setup's setting.
		int score = EffectiveAggressionSeed(self.civilization?.aggressionLevel ?? 0,
			GameDifficultyIndex(gameData), gameData?.difficulties?.Count ?? 0);

		// Section 3.1 - the reputation record.
		score += rep.HostilityContribution();

		// Section 3.1's dynamic term: +1 per unit owned by A whose civilization
		// matches B's race id. The engine records that as a unit's nationality.
		score += gameData.mapUnits.Count(unit =>
			unit.owner == self && unit.nationality != null && unit.nationality == other.civilization);

		// Section 3.2 - comparative terms.
		score += GovernmentTerm(self, other);
		if (self.civilization?.cultureGroup != null && other.civilization?.cultureGroup != null
				&& self.civilization.cultureGroup.index == other.civilization.cultureGroup.index) {
			score -= 1;
		}
		int selfCulture = self.TotalAccumulatedCulture();
		int otherCulture = other.TotalAccumulatedCulture();
		if (otherCulture < selfCulture / 2) {
			score += 1;
		} else if (otherCulture > 2 * selfCulture) {
			score -= 1;
		}

		// Section 3.3 - relation and contact flags.
		bool atWar = met && PlayerRelationship.AtWar(self, other);

		if (HasDealTargeting(other, self, DealSubType.MilitaryAlliance))
			score += 10;
		if (HasDealTargeting(other, self, DealSubType.TradeEmbargo))
			score += atWar ? 2 : 10;

		// The relation/contact word (0x44032c-0x44033b): being at war is worth +5,
		// and bit 0x10 of `A.Contacts[B]` is worth the same +5 through the same
		// branch. Only when neither holds does bit 0x8 count, for +1. The bits are
		// imported from the save's LEAD contact word; nothing in the shipped code
		// ever sets them (see spec 20 section 3.3).
		bool grievance16 = selfRel != null && selfRel.HasContactGrievance(PlayerRelationship.ContactGrievanceBit16);
		bool grievance8 = selfRel != null && selfRel.HasContactGrievance(PlayerRelationship.ContactGrievanceBit8);
		if (atWar || grievance16) {
			score += 5;
		} else if (grievance8) {
			score += 1;
		}

		// The embassy flag (the header's D50 array, written by
		// Leader_establish_embassy @ 0x56b7d0): -2 at peace, +1 at war.
		if (selfRel?.embassyEstablished == true)
			score += atWar ? 1 : -2;

		// Treaties: relation bit 1 (MPP) or bit 4 (peace) -> -10, bit 2
		// (military alliance) -> -5. In the engine peace is a multi-turn deal, so
		// "met and not at war" is the peace half (an unmet pair has no peace bit).
		bool hasMpp = HasDeal(self, other, DealSubType.MutualProtectionPact);
		if (hasMpp || (met && !atWar))
			score -= 10;
		if (HasDeal(self, other, DealSubType.MilitaryAlliance))
			score -= 5;

		// FUN_00501020: -2 per third civ that A and B are jointly allied against.
		score -= 2 * JointAllianceCount(self, other);

		// FUN_0055e8e0: the subject holds at least one resource at all.
		//
		// UNVERIFIED PROXY (see spec 20 section 11 item 13). The original walks the
		// subject's own `Available_Resources` table (leader `+0x1614`, one 0x60-byte
		// row per resource, three bytes per civ) and returns true when any row's
		// first byte for that civ is non-zero. That table is the *available*
		// (visible/connected/imported) resource set, which is a different notion
		// from "has a resource inside its borders". The engine's
		// `resourcesInBorders` is the closest available state (and is refreshed
		// only by GameData's tile-owner pass), so it stands in for the term. Tests
		// pin the current behaviour so a future change is visible.
		if (other.resourcesInBorders != null && other.resourcesInBorders.Count > 0)
			score -= 5;

		// Section 3.4 - the shared-enemy loop, the original's FIRST loop
		// (`0x440479`-`0x44051d`), which runs before the third-party loop below.
		// For every live civ p (the original's loop starts at civ 1, so the
		// barbarian slot is never visited) that A is AT WAR with:
		//
		//   * when B is also at war with p, subtract 3 plus four clamped counters
		//     of p's own reputation record toward B: caught spies capped at 2,
		//     `field_14` at 4, war-damage memory at 5 and this turn's war damage at
		//     1 (15 points at most, i.e. a full mood band on a 100x100 map);
		//   * otherwise, when B has a trade embargo against p, subtract 2.
		//
		// Sharing an enemy therefore makes A *less* hostile to B. The spec's
		// section 3.5 ("the second loop") is the third-party sum below, a
		// different rule; this loop was undocumented before this round.
		foreach (Player p in gameData.players) {
			if (p == null || p.isBarbarians || p.id == self.id)
				continue;
			// A.At_War[p] (`A+0xD30+p`, `0x440490`). The original's live-player
			// mask (`p_player_bits` @ `0xa526c0`, `0x440482`) is a separate test,
			// but the engine's AtWar() refuses a defeated player through
			// TryGetRelationship, so a cleared live bit and a defeated civ are the
			// same skip here. The original's loop index starts at 1, so the
			// barbarian slot is never visited; the isBarbarians skip above is that.
			if (!PlayerRelationship.AtWar(self, p))
				continue;

			if (PlayerRelationship.AtWar(other, p)) {
				DiplomaticReputation ofB = Reputation(p, other);
				score -= 3
					+ Math.Min(ofB.caughtSpies, 2)
					+ Math.Min(ofB.field14, 4)
					+ Math.Min(ofB.warDamageMemory, 5)
					+ Math.Min(ofB.warDamageThisTurn, 1);
			} else if (HasDealTargeting(other, p, DealSubType.TradeEmbargo)) {
				score -= 2;
			}
		}

		// Section 3.5 - third-party opinion: for every other live civ p that we
		// have met and are not at war with (or that is already dead), add p's
		// own clamped view of B. The caps are applied before summing. The guard
		// excludes A itself (`0x440566`) and the SUBJECT (`0x44056f`), not the
		// function's third argument - that one only gates the strength halving.
		foreach (Player p in gameData.players) {
			if (p.id == self.id || p.id == other.id)
				continue;
			// The shipped guard is "not at war with p, or p is already dead". The
			// engine's TryGetRelationship refuses defeated civs, so the "p is dead"
			// half is already covered by the met test below.
			if (PlayerRelationship.AtWar(self, p) && !p.defeated)
				continue;
			// Only civs we have met (Contacts bit 1): a relationship exists.
			if (!PlayerRelationship.TryGetRelationship(self, p, out _))
				continue;
			DiplomaticReputation theirs = Reputation(p, other);
			score += Math.Min(theirs.warDeclarationsAgainstUs, 2);
			score += Math.Min(theirs.cancelledDeals, 2);
			score += Math.Min(theirs.trustCounter, 3);
			score += Math.Min(theirs.aggressionObserved, 1);
			score += Math.Min(theirs.field3C1, 8);
			score += Math.Min(theirs.field3C2, 1);
		}

		// Section 3.6 special cases, in the shipped order (`0x440664`-`0x440697`).
		// The first is the at-war floor, the second the strength halving, gated on
		// the third argument's low byte being zero.
		if (score < 0 && atWar)
			score = 0;
		if (!suppressStrengthHalving && score < 0
				&& Power(other, gameData) > Power(self, gameData))
			score /= 2;

		int half = (gameData.map.numTilesWide + gameData.map.numTilesTall) / 2;
		return Math.Clamp(score, -half, half);
	}

	/// <summary>
	/// The five-level mood for a hostility score, with the exact boundaries of
	/// `Leader_get_diplomatic_mood_toward` @ `0x440ad0`: with `half` =
	/// floor((W + Ht) / 2), a score below `-floor(half/10)` is GRACIOUS, a score
	/// above `floor((W + Ht)/20)` is FURIOUS, a negative score is POLITE, zero is
	/// CAUTIOUS and a positive score is ANGRY. On a 100x100 map both thresholds
	/// are 10 (spec section 4).
	/// </summary>
	public static DiplomaticMood MoodForScore(int hostilityScore, int mapWidth, int mapHeight) {
		int half = (mapWidth + mapHeight) / 2;
		int graciousThreshold = half / 10;
		int furiousThreshold = (mapWidth + mapHeight) / 20;

		if (hostilityScore < -graciousThreshold) return DiplomaticMood.Gracious;
		if (hostilityScore > furiousThreshold) return DiplomaticMood.Furious;
		if (hostilityScore < 0) return DiplomaticMood.Polite;
		if (hostilityScore > 0) return DiplomaticMood.Angry;
		return DiplomaticMood.Cautious;
	}

	/// <summary>
	/// A's mood toward B: the attitude score of <see cref="HostilityScore"/>
	/// mapped through <see cref="MoodForScore"/>. The original's mood function
	/// relays its own second stack argument into the attitude function's halving
	/// gate (`0x440ade`), so `suppressStrengthHalving` reaches the score unchanged.
	/// </summary>
	public static DiplomaticMood MoodToward(Player self, Player other, GameData gameData, bool suppressStrengthHalving) {
		int score = HostilityScore(self, other, gameData, suppressStrengthHalving);
		return MoodForScore(score, gameData.map.numTilesWide, gameData.map.numTilesTall);
	}

	/// <summary>
	/// The reputation record of A toward B, or the shared zero record when the
	/// two have not met (the shipped engine reads the zeroed block).
	/// </summary>
	public static DiplomaticReputation Reputation(Player self, Player other) {
		return Relationship(self, other)?.reputation ?? DiplomaticReputation.Zero;
	}

	/// <summary>
	/// The relationship of A toward B, or null when the two have not met.
	/// </summary>
	public static PlayerRelationship Relationship(Player self, Player other) {
		return PlayerRelationship.TryGetRelationship(self, other, out var rel) ? rel : null;
	}

	// Section 3.2 government term. A's race's favourite government makes a
	// matching pair worth -5 and its shunned government makes a mismatch worth
	// +5; the engine stores the two government *names* on the Civilization.
	private static int GovernmentTerm(Player self, Player other) {
		string selfGov = self.government?.name;
		string otherGov = other.government?.name;
		if (selfGov == null || otherGov == null)
			return 0;
		if (selfGov == otherGov) {
			return selfGov == self.civilization?.favoriteGovernmentName ? -5 : 0;
		}
		return otherGov == self.civilization?.shunnedGovernmentName ? 5 : 1;
	}

	// The engine's stand-ins for the per-pair Military_Allies and Trade_Embargos
	// arrays: an alliance/embargo deal of `other`'s that names `self` as its
	// target.
	private static bool HasDealTargeting(Player owner, Player target, DealSubType subType) {
		PlayerRelationship rel = Relationship(owner, target);
		return rel != null && rel.multiTurnDeals.Any(deal => deal != null
			&& deal.dealSubType == subType && deal.againstPlayer == target.id);
	}

	private static bool HasDeal(Player self, Player other, DealSubType subType) {
		PlayerRelationship rel = Relationship(self, other);
		return rel != null && rel.multiTurnDeals.Any(deal => deal != null && deal.dealSubType == subType);
	}

	// FUN_00501020: how many third civs the pair are jointly allied against.
	private static int JointAllianceCount(Player self, Player other) {
		PlayerRelationship rel = Relationship(self, other);
		if (rel == null)
			return 0;
		return rel.multiTurnDeals
			.Where(deal => deal != null && deal.dealSubType == DealSubType.MilitaryAlliance && deal.againstPlayer != null)
			.Select(deal => deal.againstPlayer)
			.Distinct()
			.Count();
	}

	// The original's `power_rank` (leader + 0x24) is a per-civ ordering in which
	// a LARGER value means a STRONGER civ (the external header: "31 = strongest,
	// 30 = second strongest, ..."), and the halving branch at 0x44068c takes it
	// when A.power_rank < B.power_rank, i.e. when B is stronger.
	//
	// UNVERIFIED PROXY (see spec 20 section 11 item 12). The engine has no power_rank:
	// the closest available ordering is the last histograph `Power` entry, which
	// Player.cs already uses as the civ's power value (its `?? 100` fallback is
	// mirrored here). Power and power_rank need not rank the civs identically,
	// so the halving can fire for a pair the original would not halve. Tests pin
	// both the last-entry read and the fallback so a future change is visible.
	private static int Power(Player player, GameData gameData) {
		if (gameData.history != null && gameData.history.TryGetValue(player.id.ToString(), out var records)) {
			HistTurnRecord last = records.LastOrDefault();
			if (last != null)
				return last.Power;
		}
		return 100;
	}

	// Section 3.6 seed. `Race_get_effective_aggression_level` @ 0x53a0b0 is race
	// vtable slot 8 and returns a small integer in -2..2 that seeds the hostility
	// score. Its four inputs are:
	//
	//   * `aggressionLevel` - the race record's own AggressionLevel (`+0x918`,
	//     the BIQ RACE field the importer already carries);
	//   * the game difficulty index (`p_game_difficulty` @ `0xa52684`, 0-based -
	//     the same value the victory score multiplies by `index + 1`);
	//   * a record count divided by six (the global at `0x9c3d94`);
	//   * the game's AI Aggression setting (`0xa52b40`), a value 0..4 that the
	//     shipped setup reads from the `[Conquests]` ini key `Aggression` with a
	//     default of 2, and which the game setup screen stores on the game object
	//     at `+0x2e24`.
	//
	// OpenCiv3 carries the first two. It has NO AI Aggression setting, so the
	// band input is a named deviation and defaults to 2 (Normal), the value the
	// shipped setup passes as the ini default; that makes the band adjustment the
	// identity. The divided count at `0x9c3d94` is read but never written anywhere
	// in the shipped code, so this models it from the rules' difficulty list; spec
	// 20 section 11 items 10-11 record both deviations.
	public const int DefaultAiAggressionSetting = 2;

	/// <summary>
	/// `Race_get_effective_aggression_level` @ `0x53a0b0`: the effective aggression
	/// seed, clamped to -2..2, with the AI Aggression band applied afterwards.
	/// Bands 0 and 4 return a hard -2 and +2 without looking at the clamped value
	/// (the original's jump table entries `0x53a0e9` and `0x53a100`); bands 1 and 3
	/// shift the clamped value by -1 and +1 (`0x53a0f1`, `0x53a0fd`); band 2 and
	/// every value above 4 leave it alone.
	/// </summary>
	/// <param name="aiAggressionSetting">
	/// The game's AI Aggression setting, 0..4. Defaults to Normal, the only value
	/// OpenCiv3 can supply today.
	/// </param>
	public static int EffectiveAggressionSeed(int aggressionLevel, int gameDifficultyIndex,
			int difficultyLevelCount, int aiAggressionSetting = DefaultAiAggressionSetting) {
		// C# integer division truncates toward zero, which is what the original's
		// sign-corrected shifts do for both signs.
		int score = aggressionLevel + (gameDifficultyIndex / 2) - (difficultyLevelCount / 6);

		switch (aiAggressionSetting) {
			case 0:
				return -2;
			case 1:
				score -= 1;
				break;
			case 3:
				score += 1;
				break;
			case 4:
				return 2;
		}

		return Math.Clamp(score, -2, 2);
	}

	// The 0-based position of the game's difficulty in the rules' list, i.e. the
	// index the original keeps in `p_game_difficulty` and prints as "index + 1".
	// A game whose difficulty object is not in the list (or a test that never
	// builds one) reads as index 0, the same as the original's zeroed global.
	private static int GameDifficultyIndex(GameData gameData) {
		if (gameData?.difficulties == null || gameData.gameDifficulty == null)
			return 0;
		int index = gameData.difficulties.IndexOf(gameData.gameDifficulty);
		return index < 0 ? 0 : index;
	}
}
