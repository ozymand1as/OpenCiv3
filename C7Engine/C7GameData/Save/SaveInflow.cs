using System.Collections.Generic;

namespace C7GameData.Save;

public class SaveInflow {
	public string name { get; set; }
	public int iconRowIndex = 0;
	public List<SaveLocalYield> localYield { get; set; }

	// The Wealth build's shields-to-gold marker. It comes from the BIQ's
	// BLDG.Capitalization flag and is set on the Wealth inflow in
	// ruleset.json, because Wealth is an inflow rather than a building here.
	public bool capitalization;

	public SaveInflow() { }

	public SaveInflow(Inflow inflow) {
		this.name = inflow.name;
		this.iconRowIndex = inflow.iconRowIndex;
		this.capitalization = inflow.capitalization;
		this.localYield = inflow.localYield.ConvertAll(y => new SaveLocalYield(y.yieldType, GetYieldCalc(y.yieldType)));
	}

	public string GetYieldCalc(InflowYield yieldType) {
		return $"inflows.result.{this.name}.{yieldType}".ToLower();
	}
}
