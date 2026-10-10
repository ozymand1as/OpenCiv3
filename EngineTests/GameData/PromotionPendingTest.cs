using System;
using System.Collections.Generic;
using System.Linq;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// Guards Civ3's `Unit.Status` bit 0x2, the field's last unidentified movement
/// bit (11_movement.md §10.1, parity row G18).
///
/// The bit is "a battlefield-promotion roll has already failed for this unit
/// this turn". Unit_score_kill @ 0x5bef00 tests it before rolling
/// (`testb $0x2, 0x48(%esi)` at 0x5bf08b, `jne` straight to the promotion at
/// 0x5bf0db), sets it when the roll fails in an offline game (load the status
/// word, `orb $0x2`, store back: 0x5bf0d1-0x5bf0d6 and 0x5bf7b3), and never
/// clears it on the promotion path. Unit_begin_turn @ 0x5c7700 clears it with
/// the other per-turn status bits (`andb $-0x48, %al` at 0x5c7e7e, which masks
/// off 0x1, 0x2, 0x4 and 0x40 next to the `Unit.Moves := 0` store at 0x5c7e77).
///
/// Those three sites are the bit's whole life: a scan of the live image finds
/// exactly one reader (`testb $0x2, 0x48(reg)` at 0x5bf08b) and exactly one
/// writer (`orb $0x2` at 0x5bf0d4) of a unit's status bit 2. So it gates
/// behaviour, and the fork did not model it: a failed roll was simply forgotten.
/// </summary>
public sealed class PromotionPendingTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;
	private readonly C7GameData.GameData gd;

	// A Random whose double rolls are scripted and counted, so a forced
	// promotion can be shown to happen without asking for a roll at all.
	private class ScriptedRandom : Random {
		public readonly List<double> results = new();
		public int doubleCalls;

		public override double NextDouble() {
			doubleCalls++;
			return results.Count > 0 ? results[0] : 0.9;
		}
	}

	public PromotionPendingTest(SaveGameFixture fixture) {
		this.fixture = fixture;
		gd = fixture.saveGame.ToGameData(fixture.behaviors);
		EngineStorage.InitializeGameDataForTests(gd);
		// Promote animates, which waits on a message no test UI sends.
		EngineStorage.animationsEnabled = false;
	}

	// ---------- helpers ----------

	private Player MakePlayer(bool barbarian = false) {
		Civilization civ = new Civilization(barbarian ? "Barbarians" : "TestCiv") {
			noun = "TestCivs",
			adjective = "Test",
			isBarbarian = barbarian,
		};
		civ.traits = [];
		Player player = new Player {
			id = gd.GenerateID("player"),
			civilization = civ,
			rules = gd.rules,
			government = new Government(),
		};
		// The save round trip resolves the owner through gd.players, so the test
		// player has to be in it.
		gd.players.Add(player);
		return player;
	}

	private Tile MakeTile() {
		return new Tile(gd.GenerateID("tile")) {
			overlayTerrainType = new TerrainType { Key = "test" },
		};
	}

	private MapUnit MakeUnit(Player owner, Tile location, string experience = "Regular") {
		UnitPrototype proto = new UnitPrototype {
			name = $"TestUnit-{experience}",
			attack = 4,
			defense = 2,
			movement = 1,
		};
		proto.categories.Add("Land");

		MapUnit unit = proto.GetInstance(gd.GenerateID(proto.name), proto, owner, location: location);
		unit.experienceLevel = gd.experienceLevels.First(e => e.key == experience);
		unit.experienceLevelKey = experience;
		unit.hitPointsRemaining = unit.maxHitPoints;
		location.unitsOnTile.Add(unit);
		gd.mapUnits.Add(unit);
		owner.AddUnit(unit);
		return unit;
	}

	private ScriptedRandom UseScriptedRandom(params double[] rolls) {
		ScriptedRandom rng = new ScriptedRandom();
		rng.results.AddRange(rolls);
		C7GameData.GameData.rng = rng;
		return rng;
	}

	// A regular unit has a 0.25 promotion chance, so 0.9 always fails and 0.1
	// always succeeds.
	private const double Fail = 0.9;
	private const double Succeed = 0.1;

	// ---------- the bit is written when a roll fails ----------

	[Fact]
	public void AFailedRollSetsThePendingBitWithoutPromoting() {
		MapUnit winner = MakeUnit(MakePlayer(), MakeTile());
		MapUnit defeated = MakeUnit(MakePlayer(), MakeTile());
		UseScriptedRandom(Fail);

		winner.RollToPromote(defeated);

		Assert.True(winner.promotionPending);
		Assert.Equal("Regular", winner.experienceLevelKey);
	}

	[Fact]
	public void ASuccessfulRollPromotesWithoutSettingTheBit() {
		MapUnit winner = MakeUnit(MakePlayer(), MakeTile());
		MapUnit defeated = MakeUnit(MakePlayer(), MakeTile());
		UseScriptedRandom(Succeed);

		winner.RollToPromote(defeated);

		Assert.False(winner.promotionPending);
		Assert.Equal("Veteran", winner.experienceLevelKey);
	}

	// ---------- the bit is read before the roll ----------

	// The falsifying case for the whole row: the second victory promotes even
	// though the roll would fail, and the roll is not taken at all
	// (0x5bf08f jumps straight past the rand_int call at 0x5bf097).
	[Fact]
	public void ThePendingBitForcesTheNextPromotionWithoutARoll() {
		MapUnit winner = MakeUnit(MakePlayer(), MakeTile());
		MapUnit defeated = MakeUnit(MakePlayer(), MakeTile());
		ScriptedRandom rng = UseScriptedRandom(Fail, Fail);

		winner.RollToPromote(defeated);
		Assert.True(winner.promotionPending);
		Assert.Equal(1, rng.doubleCalls);

		winner.RollToPromote(defeated);

		Assert.Equal("Veteran", winner.experienceLevelKey);
		// The forced promotion did not ask for a roll, so the second failure
		// value was never consumed.
		Assert.Equal(1, rng.doubleCalls);
	}

	// The bit is not cleared by the promotion it forces, so a unit that has
	// already failed one roll promotes on every later victory in the same turn.
	// That is the binary's behaviour: the promotion path (0x5bf10e) increments
	// the experience level and never touches the status word.
	[Fact]
	public void TheBitIsNotClearedByThePromotionItForces() {
		MapUnit winner = MakeUnit(MakePlayer(), MakeTile());
		MapUnit defeated = MakeUnit(MakePlayer(), MakeTile());
		ScriptedRandom rng = UseScriptedRandom(Fail);

		winner.RollToPromote(defeated);
		Assert.True(winner.promotionPending);

		winner.RollToPromote(defeated);
		Assert.Equal("Veteran", winner.experienceLevelKey);
		Assert.True(winner.promotionPending);

		winner.RollToPromote(defeated);
		Assert.Equal("Elite", winner.experienceLevelKey);
		Assert.True(winner.promotionPending);
		Assert.Equal(1, rng.doubleCalls);
	}

	// The path that actually reaches the roll in a game, so the wiring is pinned
	// as well as the roll itself.
	[Fact]
	public void AVictoryThatFailsThePromotionRollLeavesTheBitSet() {
		MapUnit winner = MakeUnit(MakePlayer(), MakeTile());
		MapUnit defeated = MakeUnit(MakePlayer(), MakeTile());
		UseScriptedRandom(Fail);

		winner.RollForCombatOutcome(defeated, defeatedWasTheDefender: true);

		Assert.True(winner.promotionPending);
		Assert.Equal("Regular", winner.experienceLevelKey);
	}

	// ---------- the bit is cleared at the start of the unit's turn ----------

	[Fact]
	public void TheStartOfTheUnitsTurnClearsThePendingBit() {
		MapUnit winner = MakeUnit(MakePlayer(), MakeTile());
		MapUnit defeated = MakeUnit(MakePlayer(), MakeTile());
		ScriptedRandom rng = UseScriptedRandom(Fail, Fail);

		winner.RollToPromote(defeated);
		Assert.True(winner.promotionPending);

		winner.OnBeginTurn();
		Assert.False(winner.promotionPending);

		// With the bit clear, a failed roll is a failed roll again.
		winner.RollToPromote(defeated);
		Assert.Equal("Regular", winner.experienceLevelKey);
		Assert.True(winner.promotionPending);
		Assert.Equal(2, rng.doubleCalls);
	}

	// The cleared set is the whole status word's per-turn set, so the clearing
	// shares the movement reset (0x5c7e77-0x5c7e80).
	[Fact]
	public void TheTurnStartResetClearsTheBitWithTheMovementPoints() {
		MapUnit winner = MakeUnit(MakePlayer(), MakeTile());
		winner.promotionPending = true;
		winner.movementPoints.onConsumeAll();

		winner.OnBeginTurn();

		Assert.False(winner.promotionPending);
		Assert.Equal(winner.MaxMovementPoints, winner.movementPoints.remaining);
	}

	// ---------- the barbarian early exit ----------

	[Fact]
	public void ABarbarianNeverEarnsThePendingBit() {
		MapUnit barbarian = MakeUnit(MakePlayer(barbarian: true), MakeTile());
		MapUnit defeated = MakeUnit(MakePlayer(), MakeTile());
		ScriptedRandom rng = UseScriptedRandom(Fail);

		barbarian.RollToPromote(defeated);

		Assert.False(barbarian.promotionPending);
		Assert.Equal(0, rng.doubleCalls);
	}

	// ---------- the save round trip ----------

	// The original's status word is part of a save, so the bit survives one: a
	// reload in the middle of a turn must not lose the promotion the unit has
	// already earned.
	[Fact]
	public void ThePendingBitSurvivesASaveRoundTrip() {
		Player player = MakePlayer();
		MapUnit winner = MakeUnit(player, MakeTile());
		winner.promotionPending = true;

		SaveUnit saved = new SaveUnit(winner);
		Assert.True(saved.promotionPending);

		MapUnit reloaded = saved.ToMapUnit(gd.unitPrototypes, gd.experienceLevels,
			gd.players, gd.Terraforms, gd.map);

		Assert.True(reloaded.promotionPending);
	}

	[Fact]
	public void TheSaveRoundTripKeepsAClearBitClear() {
		MapUnit winner = MakeUnit(MakePlayer(), MakeTile());
		Assert.False(winner.promotionPending);

		SaveUnit saved = new SaveUnit(winner);
		MapUnit reloaded = saved.ToMapUnit(gd.unitPrototypes, gd.experienceLevels,
			gd.players, gd.Terraforms, gd.map);

		Assert.False(reloaded.promotionPending);
	}
}
