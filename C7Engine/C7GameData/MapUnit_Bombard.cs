using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using C7Engine;

namespace C7GameData {
	public partial class MapUnit {
		private List<AnimatedEffect> hitList = [AnimatedEffect.Hit, AnimatedEffect.Hit2, AnimatedEffect.Hit3, AnimatedEffect.Hit5];

		public enum BombardTarget {
			None,
			City,
			Unit,
			Improvement
		}

		public bool HasBombardAbility() {
			return this.unitType.actions.Contains(UnitAction.Bombard);
		}

		public bool CanBombardTile(Tile tile) {
			return CanBombardTile(tile, out _);
		}

		/// <summary>
		/// Whether this unit can bombard a tile, regardless of status, except locked alliance, because nothing can break it.<br/>
		/// Locked alliances are more protective of tiles/units etc, than even our own;<br/>
		/// While we can pillage our own tiles, we can't pillage a tile belonging to a locked ally.
		/// </summary>
		/// <param name="tile"></param>
		/// <param name="bombardTarget"></param>
		/// <returns></returns>
		public bool CanBombardTile(Tile tile, out BombardTarget bombardTarget) {
			bombardTarget = BombardTarget.None;

			if (this.location.DistanceTo(tile) > this.unitType.bombardRange)
				return false;

			if (this.unitType.bombard == 0)
				return false;

			if (tile.HasCity() && tile.cityAtTile.owner == this.owner)
				return false;

			if (tile.HasCity() && EngineStorage.gameData.AreInLockedPeace(this.owner, tile.cityAtTile.owner))
				return false;

			if (tile.HasCity())
				bombardTarget = BombardTarget.City;

			MapUnit target = tile.FindTopDefenderForBombard(this);

			// TODO: Consider colony on neutral tile (allies && potential enemies)

			if (tile.overlays.GetManMadeImprovements().Any()) {
				if (target == NONE) {
					if (tile.OwningPlayer() != null && EngineStorage.gameData.AreInLockedPeace(this.owner, tile.OwningPlayer()))
						return false;
				}

				if (target != NONE) {
					bombardTarget = BombardTarget.Unit;
					if (EngineStorage.gameData.AreInLockedPeace(this.owner, target.owner))
						return false;

					if (target.IsCombatUnit() && target.owner != this.owner) {
						return true;
					}
				}

				if (tile.OwningPlayer() != null && EngineStorage.gameData.AreInLockedPeace(this.owner, tile.OwningPlayer()))
					return false;

				bombardTarget = BombardTarget.Improvement;
			} else {
				if (target != NONE) {
					if (!target.IsCombatUnit())
						return false;
					if (target.IsCombatUnit() && target.owner == this.owner)
						return false;
					if (EngineStorage.gameData.AreInLockedPeace(this.owner, target.owner))
						return false;

					bombardTarget = BombardTarget.Unit;
				}
			}

			if (bombardTarget == BombardTarget.None)
				return false;

			return true;
		}

		public async Task Bombard(Tile tile) {
			// Could check canBombardTile(..) again, but no need really

			MapUnit target = tile.FindTopDefenderForBombard(this);

			var hasTargetUnit = target != NONE && target.owner != owner;
			var hasForeignCity = tile.HasCity() && tile.cityAtTile.owner != owner;
			var hasCityWalls = hasForeignCity && tile.cityAtTile.GetBuildings().Any(b => b.building.providesWalls);
			var hasTileImprovements = tile.HasImprovements;

			if (!(hasTargetUnit || hasTileImprovements || hasForeignCity))
				return; // Nothing to bombard

			facingDirection = location.DirectionTo(tile);

			if (hasCityWalls)
				await BombardCityWalls(tile);
			else if (hasTargetUnit)
				await BombardUnits(tile, target);
			else if (hasForeignCity)
				await BombardCity(tile);
			else
				await BombardTileImprovements(tile);
		}

		private async Task BombardCityWalls(Tile tile) {
			// CF Civilopedia: City walls have a land bombardment defense of 8
			// CF Civilopedia: Coastal defences have a land bombardment defense of 8
			// Anecdotal: "City walls are hit first."
			// TODO: Make configurable

			const int wallDefence = 8;

			var hitCount = 0;

			var walls = tile.cityAtTile.GetBuildings().First(b => b.building.providesWalls);

			double bombardStrength  = StrengthVersus(null, CombatRole.Bombard, facingDirection);
			double defenderStrength = wallDefence;
			double attackerOdds = bombardStrength / (bombardStrength + defenderStrength);
			if (Double.IsNaN(attackerOdds))
				return;

			if (tile.cityAtTile.GetBuildings().Contains(walls)) {
				await RunAnimatedBombard(tile, attackerOdds, () => {
					hitCount += 1;
					tile.cityAtTile.RemoveBuilding(walls);
				});
			}

			TriggerPopUp(hitCount, tile, $"The Walls of {tile.cityAtTile.name} have been destroyed.");
		}

		private async Task BombardUnits(Tile tile, MapUnit target) {
			// TODO: Make configurable

			movementPoints.onUnitMove(1);
			await animateAsync(AnimatedAction.ATTACK1);

			// TODO: Figure out the bombard defense that walls grant.
			double bombardStrength  = StrengthVersus(target, CombatRole.Bombard, facingDirection);
			double defenderStrength = target.StrengthVersus(this, CombatRole.BombardDefense, facingDirection);
			double attackerOdds = bombardStrength / (bombardStrength + defenderStrength);
			if (Double.IsNaN(attackerOdds))
				return;

			var tries = 0;
			var hitCount = 0;

			while (tries < unitType.rateOfFire) {
				tries++;
				if (target.hitPointsRemaining - hitCount <= 1 && tile.IsLand() && !this.unitType.isLandBombardmentLethal)
					break;
				if (target.hitPointsRemaining - hitCount <= 1 && tile.IsWater() && !this.unitType.isSeaBombardmentLethal)
					break;

				var r = GameData.rng.NextDouble();
				if (r < attackerOdds) {
					hitCount++;
				}
			}

			if (hitCount > 0) {
				for (int i = 0; i < hitCount; ++i) {
					target.hitPointsRemaining -= 1;
					await tile.AnimateAsync(this.hitList[GameData.rng.Next(0, hitList.Count)]);
				}

			} else
				await tile.AnimateAsync(tile.IsWater() ? AnimatedEffect.WaterMiss : AnimatedEffect.Miss);

			if (target.hitPointsRemaining <= 0) {
				// Destroying a unit awards the bombarding player victory points
				// (spec 25 section 4).
				owner.AwardVictoryPointsForUnitKill(EngineStorage.gameData, target);
				RollToPromote(target);
				await target.animateAsync(AnimatedAction.DEATH, AnimationEnding.Pause);
				target.RemoveFromPlay();
				// Target destroyed, skip remaining fire -- TODO: Re-target?
			}

			TriggerPopUp(hitCount, tile, "Artillery bombardment successful! Enemy units injured.");
		}

		private async Task BombardCity(Tile tile) {
			// Anecdotal: If there are no units left to hit, then citizens or buildings are hit, apparently with same probability.
			// Anecdotal: "buildings (if I remember correctly) have a defense value of 16"
			// Anecdotal: It seems population is killed off more quickly than buildings.
			// TODO: Make configurable

			const int buildingDefence = 16;
			const int populationDefence = 12;
			const float buildingOrPopulationOdds = 0.5f;

			// TODO: probably not canon to exclude palace
			List<CityBuilding> eligibleBuildingsForBombardment = tile.cityAtTile.GetBuildings().Where(b => !b.building.isCenterOfEmpire).ToList();

			var targetBuildings = GameData.rng.NextDouble() <= buildingOrPopulationOdds && eligibleBuildingsForBombardment.Count > 0;
			var defence = targetBuildings ? buildingDefence : populationDefence;
			var destroyMsg = string.Empty;
			Action remover = targetBuildings
				? () =>
				{
					var building = eligibleBuildingsForBombardment
						.OrderBy(x => GameData.rng.Next()).First();
					tile.cityAtTile.RemoveBuilding(building);
					destroyMsg = $"The {building.building.name} of {tile.cityAtTile.name} has been destroyed!";
				}
			: () =>
				{
					tile.cityAtTile.RemoveRandomCitizen();
					destroyMsg = $"Some of {tile.cityAtTile.name}'s citizens have been killed!";
				};

			var hitCount = 0;

			double bombardStrength  = StrengthVersus(null, CombatRole.Bombard, facingDirection);
			double defenderStrength = defence;
			double attackerOdds = bombardStrength / (bombardStrength + defenderStrength);
			if (Double.IsNaN(attackerOdds))
				return;

			await RunAnimatedBombard(tile, attackerOdds, () => {
				hitCount += 1;
				remover();
			});

			TriggerPopUp(hitCount, tile, destroyMsg);
		}

		// The fixed defence a bombardment rolls against, before the target
		// tile's percentages scale it (12_combat.md §6.3).
		private const int BombardTargetBaseDefense = 16;

		// The defence a bombardment rolls against on a tile: the tile's terrain
		// and city/improvement defence percentages scale a fixed defence of 16.
		// The river term and the per-civ tile-mask term never apply to a
		// bombardment (12_combat.md §6.3).
		public int BombardTargetDefense(Tile tile) {
			int percent = (int)Math.Round(100.0 * tile.overlayTerrainType.defenseBonus.amount);
			foreach (StrengthBonus bonus in tile.overlays.GetDefenseBonuses())
				percent += (int)Math.Round(100.0 * bonus.amount);
			if (tile.HasCity()) {
				foreach (StrengthBonus bonus in tile.cityAtTile.GetDefenseBonuses())
					percent += (int)Math.Round(100.0 * bonus.amount);
			}
			return (100 + percent) * BombardTargetBaseDefense / 100;
		}

		// The defender's odds in a bombardment, on the same 1/1024 scale as a
		// fight round and clamped to [1, 1023] (12_combat.md §6.3). The
		// bombarding unit then rolls RateOfFire times and the bombardment
		// succeeds as soon as one roll is at least the odds.
		public int DefenderOddsAgainstBombard(Tile tile) {
			int target = BombardTargetDefense(tile);
			int bombard = unitType.bombard;
			if (bombard + target <= 0)
				return MinCombatOdds;
			return Math.Clamp(target * CombatOddsScale / (bombard + target), MinCombatOdds, MaxCombatOdds);
		}

		private async Task BombardTileImprovements(Tile tile) {
			var improvement = tile.overlays.GetManMadeImprovements()
				.OrderBy(x => GameData.rng.Next()).FirstOrDefault();

			// Every shot rolls independently against the defender's odds and
			// the bombardment succeeds as soon as one shot hits; RateOfFire is
			// what makes bigger artillery better at tearing down improvements
			// (12_combat.md §6.3).
			int odds = DefenderOddsAgainstBombard(tile);
			var hitCount = 0;
			for (int shot = 0; shot < unitType.rateOfFire; ++shot) {
				if (GameData.rng.Next(CombatOddsScale) >= odds) {
					hitCount = 1;
					break;
				}
			}

			await animateAsync(AnimatedAction.ATTACK1);
			movementPoints.onUnitMove(1);

			if (hitCount > 0) {
				await tile.AnimateAsync(this.hitList[GameData.rng.Next(0, hitList.Count)]);
				// Remove top improvement
				tile.overlays.Remove(improvement);
				// "Replace" with downgraded improvement if it exists
				tile.overlays.Add(improvement?.upgradesFrom);
				// TODO: Re-target?
			} else {
				await tile.AnimateAsync(tile.IsWater() ? AnimatedEffect.WaterMiss : AnimatedEffect.Miss);
			}

			TriggerPopUp(hitCount, tile, $"Artillery bombardment successful! Destroyed {improvement?.key}.");
		}

		private async Task RunAnimatedBombard(Tile tile, double attackerOdds, Action callback) {
			await animateAsync(AnimatedAction.ATTACK1);
			movementPoints.onUnitMove(1);
			if (GameData.rng.NextDouble() < attackerOdds) {
				await tile.AnimateAsync(this.hitList[GameData.rng.Next(0, hitList.Count)]);
				callback();
			} else
				await tile.AnimateAsync(tile.IsWater() ? AnimatedEffect.WaterMiss : AnimatedEffect.Miss);
		}

		private void TriggerPopUp(int hitCount, Tile tile, string successMessage) {
			if (owner.isHuman) {
				if (hitCount > 0)
					new MsgShowTemporaryPopup(successMessage, tile).send();
				else
					new MsgShowTemporaryPopup($"Artillery bombardment failed.", tile).send();
			}
		}

	}
}
