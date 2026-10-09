using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;
using C7GameData.AIData;

namespace C7GameData;

public partial class MapUnit {
	public void OnBeginTurn(bool skipTurn = false) {
		int maxMP = unitType.movement;
		if (movementPoints.remaining >= maxMP && !skipTurn) {
			int maxHP = maxHitPoints;
			if (hitPointsRemaining < maxHP)
				hitPointsRemaining += HealRateAt(location);
			if (hitPointsRemaining > maxHP)
				hitPointsRemaining = maxHP;
		}

		if (skipTurn) {
			movementPoints.skipTurn();
		} else {
			movementPoints.reset(maxMP);
		}

		defensiveBombardsRemaining = 1;
	}

	public void OnEnterTile(Tile tile) {
		//Add to player knowledge of tiles
		owner.tileKnowledge.AddTilesToKnown(tile);

		// Pop a goody hut. The hut is consumed by the entering unit; the
		// barbarian player never collects its own huts.
		if (tile.hasGoodyHut && !owner.isBarbarians) {
			animate(MapUnit.AnimatedAction.VICTORY);
			GoodyHutInteractions.Consume(EngineStorage.gameData, owner, tile);
		}

		// Disperse barb camp
		if (tile.hasBarbarianCamp && !owner.isBarbarians) {
			EngineStorage.gameData.map.barbarianCamps.Remove(tile);
			tile.hasBarbarianCamp = false;
			animate(MapUnit.AnimatedAction.VICTORY);

			// TODO: make this configurable
			owner.gold += 25;
			if (owner.isHuman) {
				new MsgShowMilitaryAdvisorPopup($"We cleared a barbarian encampment and earned 25 gold!", happy: true).send();
			}
		}

		// Destroy the enemy city on the tile unless we're the barbarians,
		// in which case we'll just take some gold.
		if (tile.HasCity() && !owner.IsAtPeaceWith(tile.cityAtTile.owner)) {
			if (owner.isBarbarians) {
				// TODO: Add rules for how much gold is taken.
				int goldTaken = tile.cityAtTile.owner.gold / 4;
				tile.cityAtTile.owner.gold -= goldTaken;
				this.RemoveFromPlay();
				if (tile.cityAtTile.owner.isHuman) {
					new MsgShowMilitaryAdvisorPopup($"Barbarians have stolen {goldTaken} gold from our cities!\nWe need a stronger military.", happy: false).send();
				}
			} else {
				// Destination resolution of an enemy city: capture it
				// (spec 11 section 5, step 11), rather than destroying it.
				CityInteractions.CaptureCity(tile.cityAtTile, owner);
			}
		}

		// Check to see if we've discovered a new civ.
		//
		// TODO: this should really be based on interactions with our "visible"
		// tiles. Also civ3 only counts border-based discovery from rank 1
		// tiles, not rank 2+.
		foreach (Tile t in tile.neighbors.Values) {
			if (t.unitsOnTile.Count > 0 && owner != t.unitsOnTile[0].owner) {
				owner.EnsureRelationshipExists(t.unitsOnTile[0].owner);
			}
			if (t.owningCity != null && owner != t.owningCity.owner) {
				owner.EnsureRelationshipExists(t.owningCity.owner);
			}
		}
	}

	public void Fortify() {
		ResetFacingDirection();
		isFortified = true;
		animate(MapUnit.AnimatedAction.FORTIFY);
	}

	public void Wake() {
		isFortified = false;
	}

	public void Automate() {
		Wake();
		isAutomated = true;
		WorkerAIData? maybeAiData = WorkerAI.MakeAiData(this, owner);
		if (maybeAiData == null) {
			log.Information($"Could not find anything to automate for {this} owned by {owner}");
			isAutomated = false;
			return;
		}
		currentAI = new WorkerAI(maybeAiData);
		PlayAutomatedTurn();
	}

	public void Explore() {
		Wake();
		isAutomated = true;
		ExplorerAIData? maybeAiData = ExplorerAI.MaybeMakeAiData(this, owner);
		if (maybeAiData == null) {
			log.Information($"Could not find anything to explore for {this} owned by {owner}");
			isAutomated = false;
			return;
		}
		currentAI = new ExplorerAI(maybeAiData);
		PlayAutomatedTurn();
	}

	public void SkipTurn() {
		movementPoints.skipTurn();
	}

	public async Task Disband() {
		await EngineStorage.gameData.DisbandUnit(this);
	}

	public void RemoveFromPlay() {
		EngineStorage.gameData.RemoveUnit(this);
	}

	public async Task MoveAlongPath() {
		while (movementPoints.canMove && path?.PathLength() > 0) {
			TileDirection dir = location.DirectionTo(path.Next());
			await Move(dir, true); //TODO: don't wait on last move animation?
		}
	}

	public async Task SetUnitPath(TilePath path) {
		this.path = path;
		await MoveAlongPath();
	}

	public async void PlayAutomatedTurn() {
		if (currentAI == null) {
			// TODO: handle giving automated workers from loaded saves the
			// proper unit ai.
			isAutomated = false;
			return;
		}
		UnitAI.Result result = await currentAI.PlayTurn(owner, this);
		if (result == UnitAI.Result.Done) {
			if (currentAI is WorkerAI) {
				Automate();
			} else if (currentAI is ExplorerAI) {
				Explore();
			}
		}

		// Do nothing after an error so control returns to the player, and
		// nothing after an progress result, so that next turn continues the
		// AI action.
	}

	public async Task PerformBusyAction() {
		if (isFortified) {
			return;
		}

		if (path != null && path.PathLength() > 0) {
			await MoveAlongPath();
			return;
		}

		if (isAutomated) {
			// workers contribute their work at the end of the turn, not when assigned
			if (this.unitType.isWorker && WorkerJob != null) {
				return;
			}
			PlayAutomatedTurn();
			return;
		}
	}

	public async Task PerformEndOfTurnAction() {
		// Busy Worker
		if (WorkerJob != null) {
			WorkerProgressTowardsJob += workerSpeed();
			movementPoints.onConsumeAll();

			// See if this worker finished the job.
			if ((int)SumWorkerProgress(location, WorkerJob) >= GetWorkerJobCost(location, WorkerJob)) {
				location.FinishWorkerJob(WorkerJob);
			}
		}
	}

	/// <summary>
	/// Moves the unit in the given direction
	/// </summary>
	/// <param name="unit"></param>
	/// <param name="dir">Which direction to move, e.g. northeast, west, etc.</param>
	/// <param name="wait">Whether the method should wait to return until animations complete</param>
	/// <returns>True if the unit is alive after the movement, false otherwise</returns>
	/// <exception cref="Exception"></exception>
	public async Task<bool> Move(TileDirection dir, bool wait = false) {
		(int dx, int dy) = dir.ToCoordDiff();

		Tile newLoc = EngineStorage.gameData.map.tileAt(dx + location.XCoordinate, dy + location.YCoordinate);

		var canMove = (newLoc != Tile.NONE) && this.CanEnter(newLoc) && (movementPoints.canMove);
		if (!canMove) return false;

		facingDirection = dir;
		Wake();

		// Trigger combat if the tile we're moving into has an enemy  Or if this unit can't fight, do nothing.
		MapUnit defender = newLoc.FindTopDefender(this);
		if (defender != MapUnit.NONE && !owner.IsAtPeaceWith(defender.owner)) {
			if (unitType.attack <= 0) {
				return true;
			}

			CombatResult combatResult = await Fight(defender);
			this.path = TilePath.NONE;
			// If we were killed then of course there's nothing more to do. If the combat couldn't happen for whatever
			// reason, just give up on trying to move.
			if (combatResult == CombatResult.AttackerKilled) {
				return false;
			}
			if (combatResult == CombatResult.Impossible) {
				return true;
			}

			// If the enemy was defeated, check if there is another enemy on the tile. If so we can't complete the move
			// but still pay one movement point for the combat.
			if (combatResult == CombatResult.DefenderKilled || combatResult == CombatResult.DefenderRetreated) {
				this.movementPoints.onUnitMove(1);
				if (newLoc.FindTopDefender(this) != MapUnit.NONE) {
					this.facingDirection = this.facingDirection.Reversed();
					return true;
				}

				// Similarly if we retreated, pay one MP for the combat but don't move.
			} else if (combatResult == CombatResult.AttackerRetreated) {
				this.movementPoints.onUnitMove(1);
				this.facingDirection = this.facingDirection.Reversed();
				return true;
			}
		}

		facingDirection = dir;
		float movementCost = TilePath.GetMovementCost(this.owner, location, dir, newLoc);

		// Leave old tile
		if (!location.unitsOnTile.Remove(this))
			throw new System.Exception("Failed to remove unit from tile it's supposed to be on");

		// Move transported units, too
		if (CanTransport()) {
			var transported = location.unitsOnTile
					.Where(u => u.IsLoadedIn(this)).ToList();

			foreach (var tu in transported) {
				if (!location.unitsOnTile.Remove(tu))
					throw new System.Exception("Failed to remove unit from tile during transport move");
				newLoc.unitsOnTile.Add(tu);
				tu.location = newLoc;
			}
		}

		TryBoardingTransportOnTile(newLoc);
		TryUnboardingTransportToTile(newLoc);

		// Enter new tile
		// Make sure the unit is on the new location before claiming we have entered the tile
		newLoc.unitsOnTile.Add(this);
		location = newLoc;
		OnEnterTile(newLoc);

		if (wait)
			await animateAsync(MapUnit.AnimatedAction.RUN);
		else
			animate(MapUnit.AnimatedAction.RUN);

		movementPoints.onUnitMove(movementCost);

		return true;
	}

	public async Task<CombatResult> Fight(MapUnit defender) {
		var attacker = this;

		// Set combat animation facing. We'll restore the defender's original facing direction at the end of the battle.
		TileDirection attackerAttackDirection = attacker.location.DirectionTo(defender.location);
		TileDirection defenderDefenseDirection = attackerAttackDirection.Reversed();
		var defenderOriginalDirection = defender.facingDirection;
		attacker.facingDirection = attacker.GetAttackAnimationDirection(attackerAttackDirection);
		defender.facingDirection = defender.GetAttackAnimationDirection(defenderDefenseDirection);

		IEnumerable<StrengthBonus> attackBonuses  = attacker.ListStrengthBonusesVersus(defender, CombatRole.Attack , attackerAttackDirection),
								   defenseBonuses = defender.ListStrengthBonusesVersus(attacker, CombatRole.Defense, attackerAttackDirection);

		var (attackerEffective, defenderEffective) =
			EffectiveCombatStrengths(attacker, defender, attackerAttackDirection);

		log.Information($"Combat log: {attacker} ({attackerEffective / 100.0}) attacking {defender} ({defenderEffective / 100.0})");
		log.Information($"\tAttacker: {attacker.unitType.name}, base strength {attacker.BaseStrength(CombatRole.Attack)}");
		foreach (StrengthBonus bonus in attackBonuses)
			log.Information($"\t\t+{100.0 * bonus.amount}%\t{bonus.description}");
		log.Information($"\tDefender: {defender.unitType.name}, base strength {defender.BaseStrength(CombatRole.Defense)}");
		foreach (StrengthBonus bonus in defenseBonuses)
			log.Information($"\t\t+{100.0 * bonus.amount}%\t{bonus.description}");

		CombatResult result = CombatResult.Impossible;

		if (attackerEffective + defenderEffective <= 0)
			return result;

		// The defender's win probability in units of 1/1024: every round rolls
		// rand_int(1024) and a roll below the odds means the defender wins the
		// round (12_combat.md §2, §6.2).
		int defenderOdds = DefenderCombatOdds(attacker, defender, attackerAttackDirection);

		// Defensive bombard
		MapUnit defensiveBombarder = MapUnit.NONE;
		double defensiveBombarderStrength = 0.0;
		foreach (MapUnit candidate in defender.location.unitsOnTile.Where(u => u != defender && !u.owner.IsAtPeaceWith(attacker.owner) && u.defensiveBombardsRemaining > 0)) {
			double strength = candidate.StrengthVersus(attacker, CombatRole.DefensiveBombard, defenderDefenseDirection);
			if (strength > defensiveBombarderStrength) {
				defensiveBombarder = candidate;
				defensiveBombarderStrength = strength;
			}
		}
		// In the original game, defensive bombard does not trigger against attackers with 1 HP. See:
		// https://github.com/C7-Game/Prototype/pull/250#discussion_r893051111
		if (defensiveBombarder != MapUnit.NONE && attacker.hitPointsRemaining > 1) {
			var dBOriginalDirection = defensiveBombarder.facingDirection;
			TileDirection defensiveBombardDirection = defenderDefenseDirection;
			defensiveBombarder.facingDirection = defensiveBombarder.GetAttackAnimationDirection(defensiveBombardDirection);

			await defensiveBombarder.animateAsync(MapUnit.AnimatedAction.ATTACK1);

			// dADB = defense Against Defensive Bombard
			double dADB = attacker.StrengthVersus(defensiveBombarder, CombatRole.DefensiveBombardDefense, defensiveBombardDirection);
			if (GameData.rng.NextDouble() < defensiveBombarderStrength / (defensiveBombarderStrength + dADB))
				attacker.hitPointsRemaining -= 1;

			defensiveBombarder.defensiveBombardsRemaining -= 1;
			defensiveBombarder.facingDirection = dBOriginalDirection;
		}

		// Retreat permission (12_combat.md §6.1): a unit type may retreat only
		// when it moves faster than one tile per turn; when both sides may
		// retreat neither does, and the defender additionally loses its
		// permission on a tile that holds a city.
		bool attackerMayRetreat = attacker.CanRetreat();
		bool defenderMayRetreat = defender.CanRetreat();
		if (attackerMayRetreat && defenderMayRetreat) {
			attackerMayRetreat = false;
			defenderMayRetreat = false;
		}
		if (defender.location.HasCity())
			defenderMayRetreat = false;

		// Do combat rounds. Every round removes exactly one hit point from the
		// loser of the round and the fight ends as soon as either side reaches
		// zero; RateOfFire takes no part here, it is bombard-only
		// (12_combat.md §5, §6.2).
		while (true) {
			defender.animate(MapUnit.AnimatedAction.ATTACK1);
			await attacker.animateAsync(MapUnit.AnimatedAction.ATTACK1);
			if (GameData.rng.Next(CombatOddsScale) < defenderOdds) {
				// The defender wins the round; the attacker loses a hit point.
				attacker.hitPointsRemaining -= 1;
				if (attacker.hitPointsRemaining <= 0) {
					result = CombatResult.AttackerKilled;
					break;
				}
				// A losing attacker escapes only at exactly one remaining hit
				// point, only against an opponent above one, never as the
				// barbarians, and only with its type's retreat permission:
				// rand_int(defenderBonus + 50) < attackerBonus.
				if (attacker.hitPointsRemaining == 1 && attackerMayRetreat &&
					!attacker.owner.isBarbarians && defender.hitPointsRemaining > 1 &&
					GameData.rng.Next(defender.experienceLevel.retreatBonus + RetreatChanceDenominatorOffset) < attacker.experienceLevel.retreatBonus) {
					result = CombatResult.AttackerRetreated;
					break;
				}
			} else {
				// The attacker wins the round; the defender loses a hit point.
				defender.hitPointsRemaining -= 1;
				if (defender.hitPointsRemaining <= 0) {
					result = CombatResult.DefenderKilled;
					break;
				}
				if (defender.hitPointsRemaining == 1 && defenderMayRetreat &&
					!defender.owner.isBarbarians && attacker.hitPointsRemaining > 1) {
					// TODO: Defender retreat behavior requires some more work. There's an issue for it here:
					// https://github.com/C7-Game/Prototype/issues/274
					Tile retreatDestination = defender.location.neighbors[attackerAttackDirection];
					if ((retreatDestination != Tile.NONE) && defender.CanEnter(retreatDestination) &&
						GameData.rng.Next(attacker.experienceLevel.retreatBonus + RetreatChanceDenominatorOffset) < defender.experienceLevel.retreatBonus) {
						await defender.Move(attackerAttackDirection, true);
						result = CombatResult.DefenderRetreated;
						break;
					}
				}
			}
		}

		if ((result == CombatResult.AttackerKilled) || (result == CombatResult.DefenderKilled)) {
			var (dead, alive) = (result == CombatResult.AttackerKilled) ? (attacker, defender) : (defender, attacker);
			// Destroying another civilization's unit awards the killer victory
			// points (spec 25 section 4). Civ3 credits this in its unit death
			// handler, which is where this combat outcome leads.
			alive.owner.AwardVictoryPointsForUnitKill(EngineStorage.gameData, dead);
			alive.RollForCombatOutcome(dead, defeatedWasTheDefender: result == CombatResult.DefenderKilled);
			await dead.animateAsync(MapUnit.AnimatedAction.DEATH);
			dead.RemoveFromPlay();
		}

		if (result.DefenderWon())
			defender.facingDirection = defenderOriginalDirection;

		return result;
	}

	public async Task<City?> BuildCity(string cityName) {
		if (!canBuildCity()) {
			log.Warning($"can't build city at {location}");
			return null;
		}

		await animateAsync(MapUnit.AnimatedAction.BUILD);

		// TODO: Need to check somewhere that this unit is allowed to build a city on its current tile. Either do that here or in every caller
		// (probably best to just do it here).
		City city = CityInteractions.BuildCity(location, owner, cityName);
		this.RemoveFromPlay();

		return city;
	}

	// entry point for "manual" job assignment
	public void PerformTerraformAction(Terraform terraform) {
		if (!CanPerformTerraformAction(terraform)) {
			log.Warning($"can't perform {terraform.Name} by {this}");
			return;
		}
		WorkerJob = terraform;

		if (terraform.Animation is AnimatedAction animation)
			animate(animation, AnimationEnding.Repeat);

		movementPoints.onConsumeAll();

		// See if this worker finished the job.
		var terraformProgress = this.SumWorkerProgress(this.location, this.WorkerJob);
		var turnProgress = this.location.GetCurrentUnaccountedJobProgress(terraform);
		var totalCost = (float)GetWorkerJobCost(this.location, this.WorkerJob);

		if (terraformProgress + turnProgress == totalCost) {
			location.FinishWorkerJob(WorkerJob);
		}

		Wake();
		_ = PerformBusyAction();
	}

	public void BoardTransport(MapUnit t) {
		if (t == null) {
			// TODO: throw new System.Exception("Failed to find a transport to move to");
			log.Warning("Failed to find a transport to board");
			return;
		}
		t.Board(this);
		isFortified = true;
		ResetFacingDirection();
		if (this.owner.isHuman)
			new MsgUnitMoved(this).send();
	}

	public void UnboardTransport(MapUnit t) {
		if (t == null) {
			// TODO: throw new System.Exception("Failed to find the transport to unboard from");
			log.Warning("Failed to find a transport to unboard");
			return;
		}
		t.Unboard(this);
		Wake();
		if (this.owner.isHuman)
			new MsgUnitMoved(this).send();
	}

	public void TryBoardingTransportOnTile(Tile newLoc) {
		var enteringCity = newLoc.HasCity() && newLoc != location;
		if (enteringCity || !CanBoardTransportOnTile(newLoc))
			return;

		var t = SelectTransportToBoard(newLoc);
		BoardTransport(t);
	}

	public void TryUnboardingTransportToTile(Tile newLoc) {
		if (!CanUnboardTransportToTile(newLoc))
			return;

		var t = FindTransportToUnboard(this.location, this.loadedOnUnitId);
		UnboardTransport(t);
	}

	/// <summary>
	/// Boards unit into this transport
	/// </summary>
	/// <param name="mapUnit">The unit to load on a transport</param>
	private void Board(MapUnit mapUnit) {
		mapUnit.loadedOnUnitId = this.id;
		// TODO: consume moves?
	}

	/// <summary>
	/// Unloads a unit from this transport
	/// </summary>
	/// <param name="mapUnit">The unit to unload from a transport</param>
	private void Unboard(MapUnit mapUnit) {
		mapUnit.loadedOnUnitId = null;
		// TODO: consume moves?
	}
}
