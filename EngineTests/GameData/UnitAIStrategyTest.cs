using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using C7Engine;
using C7GameData;
using C7GameData.Save;
using EngineTests.Utils;
using QueryCiv3;
using QueryCiv3.Biq;
using Xunit;

namespace EngineTests.GameData;

/// <summary>
/// The Civ3 AI picks a unit's behaviour from the AI-strategy bitmask in the
/// PRTO record, not from the unit's name. These tests pin the ruleset data to
/// the shipped conquests.biq and tie the name-based decisions the AI used to
/// make to the imported bits.
/// </summary>
public class UnitAIStrategyTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public UnitAIStrategyTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	// One representative unit for every AI-strategy bit that survives into
	// ruleset.json, read off the shipped conquests.biq with the QueryCiv3 probe.
	// The Naval Missile Transport bit has no representative: its only carrier is
	// a second "Nuclear Submarine" PRTO entry that ImportUnitPrototypes drops
	// because a unit of that name was already added (the temporary
	// de-duplication described at ImportCiv3.ImportUnitPrototypes).
	private static readonly Dictionary<string, string> ProbeVerifiedStrategies = new() {
		["Settler"] = "settle",
		["Worker"] = "terraform",
		["Scout"] = "explore",
		["Explorer"] = "explore",
		["Warrior"] = "offense",
		["Spearman"] = "defense",
		["Catapult"] = "artillery",
		["Cruise Missile"] = "cruiseMissile",
		["Bomber"] = "airBombard",
		["Fighter"] = "airDefense",
		["Frigate"] = "navalPower",
		["Helicopter"] = "airTransport",
		["Galley"] = "navalTransport",
		["Carrier"] = "navalCarrier",
		["Leader"] = "leader",
		["Army"] = "army",
		["Tactical Nuke"] = "tacticalNuke",
		["ICBM"] = "icbm",
		["Princess"] = "flagUnit",
		["Lincoln"] = "king",
	};

	[Fact]
	public void UnitPrototypeCarriesAIStrategiesFromSavedPrototype() {
		UnitPrototype unit = new(new() {
			name = "Settler",
			aiStrategies = [SaveUnitPrototype.AIStrategy.Settle],
		}, []);

		Assert.Contains(SaveUnitPrototype.AIStrategy.Settle, unit.aiStrategies);
		Assert.True(unit.isAISettler);
		Assert.False(unit.isAIWorker);
		Assert.False(unit.isAIArtillery);
	}

	[Fact]
	public void BaseRulesetAssignsProbeVerifiedAIStrategies() {
		using JsonDocument ruleset = JsonUtils.LoadBaseRuleset();
		JsonElement unitPrototypes = ruleset.RootElement.GetProperty("unitPrototypes");

		Dictionary<string, List<string>> strategies = unitPrototypes.EnumerateArray()
			.ToDictionary(
				unitPrototype => unitPrototype.GetProperty("name").GetString(),
				AIStrategies);

		foreach ((string unitName, string strategy) in ProbeVerifiedStrategies) {
			Assert.True(strategies.ContainsKey(unitName), $"{unitName} is missing from the base ruleset.");
			Assert.Equal(new[] { strategy }, strategies[unitName]);
		}

		// The shipped rules use the mask as a classification: exactly one bit
		// per unit type, never an overlapping role set (spec 21_ai.md §2.2).
		foreach ((string unitName, List<string> unitStrategies) in strategies) {
			Assert.True(unitStrategies.Count == 1, $"{unitName} should have exactly one AI strategy, got [{string.Join(", ", unitStrategies)}].");
		}

		Assert.DoesNotContain(strategies.Values.SelectMany(s => s), s => s == "navalMissileTransport");
	}

	[Fact]
	public void RuntimePrototypesCarryTheRulesetAIStrategies() {
		List<UnitPrototype> prototypes = RuntimePrototypes();

		Assert.True(prototypes.Single(p => p.name == "Settler").isAISettler);
		Assert.True(prototypes.Single(p => p.name == "Worker").isAIWorker);

		// The Artillery strategy covers the whole artillery line, not only the
		// unit whose name the old AI test happened to single out.
		string[] artillery = ["Catapult", "Cannon", "Artillery", "Radar Artillery", "Hwach'a", "Trebuchet"];
		foreach (string unitName in artillery) {
			Assert.True(prototypes.Single(p => p.name == unitName).isAIArtillery, $"{unitName} should carry the Artillery AI strategy.");
		}

		Assert.True(prototypes.Single(p => p.name == "Warrior").HasAIStrategy(SaveUnitPrototype.AIStrategy.Offense));
		Assert.True(prototypes.Single(p => p.name == "Spearman").HasAIStrategy(SaveUnitPrototype.AIStrategy.Defense));
		Assert.True(prototypes.Single(p => p.name == "Scout").HasAIStrategy(SaveUnitPrototype.AIStrategy.Explore));
		Assert.True(prototypes.Single(p => p.name == "Galley").HasAIStrategy(SaveUnitPrototype.AIStrategy.NavalTransport));
	}

	[Fact]
	public void FlagBasedUnitRolesMatchTheOldNameBasedChoicesForTheShippedUnits() {
		List<UnitPrototype> prototypes = RuntimePrototypes();

		// The classification PlayerAI.GetAIForUnit used before this change.
		static string NameBasedRole(UnitPrototype prototype) => prototype.name switch {
			"Settler" => "settler",
			"Worker" => "worker",
			"Catapult" => "artillery",
			_ => "none",
		};

		// The classification it uses now, from the imported AI-strategy bits.
		static string FlagBasedRole(UnitPrototype prototype) {
			if (prototype.isAISettler) return "settler";
			if (prototype.isAIWorker) return "worker";
			if (prototype.isAIArtillery) return "artillery";
			return "none";
		}

		// The three units the AI used to key on keep the same role.
		Assert.Equal("settler", FlagBasedRole(prototypes.Single(p => p.name == "Settler")));
		Assert.Equal("worker", FlagBasedRole(prototypes.Single(p => p.name == "Worker")));
		Assert.Equal("artillery", FlagBasedRole(prototypes.Single(p => p.name == "Catapult")));

		// No unit gains the Settle or Terraform role, and the only units whose
		// role changes are the other artillery-line units, which now share the
		// Catapult's role instead of falling through to the generic chain.
		Assert.DoesNotContain(prototypes.Where(p => p.name != "Settler"), p => p.isAISettler);
		Assert.DoesNotContain(prototypes.Where(p => p.name != "Worker"), p => p.isAIWorker);

		List<string> changedRoles = prototypes
			.Where(p => NameBasedRole(p) != FlagBasedRole(p))
			.Select(p => p.name)
			.OrderBy(name => name)
			.ToList();
		Assert.Equal(new[] { "Artillery", "Cannon", "Hwach'a", "Radar Artillery", "Trebuchet" }, changedRoles);
	}

	[SkippableFact]
	public void BaseRulesetAIStrategiesMatchTheShippedBiq() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// ImportUnitPrototypes keeps the first prototype of each name, so the
		// ruleset can only carry the strategy of that first occurrence.
		Dictionary<string, List<string>> fromBiq = new();
		foreach (PRTO prto in biq.Prto) {
			fromBiq.TryAdd(prto.Name, BiqAIStrategies(prto));
		}

		using JsonDocument ruleset = JsonUtils.LoadBaseRuleset();
		Dictionary<string, List<string>> fromRuleset = ruleset.RootElement.GetProperty("unitPrototypes")
			.EnumerateArray()
			.ToDictionary(
				unitPrototype => unitPrototype.GetProperty("name").GetString(),
				AIStrategies);

		Assert.Equal(fromBiq.Keys.OrderBy(name => name), fromRuleset.Keys.OrderBy(name => name));
		foreach ((string unitName, List<string> biqStrategies) in fromBiq) {
			Assert.Equal(biqStrategies, fromRuleset[unitName]);
		}
	}

	[SkippableFact]
	public void BiqAIStrategiesImportEveryPRTOBit() {
		Skip.If(Civ3TestData.ShouldSkipCiv3DependentTests(), "No Civ3 install found.");

		BiqData biq = BiqData.LoadFile(PathUtils.defaultBicPath);

		// The importer reads the same PRTO bits the probe does, so the whole
		// section must round-trip bit for bit, not just the sampled units.
		foreach (PRTO prto in biq.Prto) {
			List<string> imported = ImportCiv3.GetUnitAIStrategies(prto)
				.Select(strategy => JsonNamingPolicy.CamelCase.ConvertName(strategy.ToString()))
				.ToList();
			Assert.Equal(BiqAIStrategies(prto), imported);
		}

		foreach ((string unitName, string strategy) in ProbeVerifiedStrategies) {
			PRTO prto = biq.Prto.First(p => p.Name == unitName);
			List<string> imported = ImportCiv3.GetUnitAIStrategies(prto)
				.Select(s => JsonNamingPolicy.CamelCase.ConvertName(s.ToString()))
				.ToList();
			Assert.Equal(new[] { strategy }, imported);
		}
	}

	private List<UnitPrototype> RuntimePrototypes() {
		return SaveGameFixture.HydrateSaveGame(fixture.saveGame).unitPrototypes;
	}

	private static List<string> AIStrategies(JsonElement unitPrototype) {
		if (!unitPrototype.TryGetProperty("aiStrategies", out JsonElement aiStrategies)) {
			return [];
		}

		return aiStrategies.EnumerateArray()
			.Select(strategy => strategy.GetString())
			.ToList();
	}

	private static List<string> BiqAIStrategies(PRTO prto) {
		List<string> result = [];
		if (prto.AIOffense) result.Add("offense");
		if (prto.AIDefense) result.Add("defense");
		if (prto.AIArtillery) result.Add("artillery");
		if (prto.AIExplore) result.Add("explore");
		if (prto.AIArmy) result.Add("army");
		if (prto.AICruiseMissile) result.Add("cruiseMissile");
		if (prto.AIAirBombard) result.Add("airBombard");
		if (prto.AIAirDefense) result.Add("airDefense");
		if (prto.AINavalPower) result.Add("navalPower");
		if (prto.AIAirTransport) result.Add("airTransport");
		if (prto.AINavalTransport) result.Add("navalTransport");
		if (prto.AINavalCarrier) result.Add("navalCarrier");
		if (prto.AITerraform) result.Add("terraform");
		if (prto.AISettle) result.Add("settle");
		if (prto.AILeader) result.Add("leader");
		if (prto.AITacticalNuke) result.Add("tacticalNuke");
		if (prto.AIICBM) result.Add("icbm");
		if (prto.AINavalMissileTransport) result.Add("navalMissileTransport");
		if (prto.AIFlag) result.Add("flagUnit");
		if (prto.AIKing) result.Add("king");
		return result;
	}
}
