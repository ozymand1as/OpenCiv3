using System.IO;
using C7GameData.Save;
using EngineTests.Utils;
using Xunit;

namespace EngineTests.GameData;

public class ResearchTest : IClassFixture<SaveGameFixture> {
	private readonly SaveGameFixture fixture;

	public ResearchTest(SaveGameFixture fixture) {
		this.fixture = fixture;
	}

	private static string WriteSave(SaveGame save, string fileName) {
		string path = PathUtils.getDataPath($"output/{fileName}");
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		save.Save(path);
		return path;
	}

	[Fact]
	public void FreeTechsRemainingRoundTripsThroughJson() {
		SaveGame save = fixture.saveGame.Clone();
		save.Players[0].freeTechsRemaining = 3;

		string path = WriteSave(save, "research_free_techs.json");
		SaveGame loaded = SaveGame.Load(path, unused => unused);
		File.Delete(path);

		Assert.Equal(3, loaded.Players[0].freeTechsRemaining.Value);
	}

	[Fact]
	public void FreeTechsRemainingIsOmittedWhenZero() {
		SaveGame save = fixture.saveGame.Clone();
		save.Players[0].freeTechsRemaining = null;

		string path = WriteSave(save, "research_no_free_techs.json");
		string json = File.ReadAllText(path);
		File.Delete(path);

		Assert.DoesNotContain("freeTechsRemaining", json);
	}
}
