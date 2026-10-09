using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace EngineTests.Utils;

/// <summary>
/// The SAV fixtures the suite downloads on demand.
///
/// <para>Tests that need one must ask for it here rather than assuming another
/// test has already fetched it. <c>EngineTests/data/</c> is gitignored, so on a
/// cold checkout a test that only checks <c>File.Exists</c> silently skips until
/// some other test happens to download the file — which makes the suite's result
/// depend on test ordering, and hides the test on a fresh clone.</para>
/// </summary>
public static class SampleSaves {

	// The hash pins the exact revision, so a changed or truncated download is
	// re-fetched rather than used.
	private const string DriveId = "1QlIavkLtPZEIv1kHK9sO0fY2yp3o2si7";
	private const string SampleSaveName = "12345.SAV";
	private const string SampleSaveMd5 = "d34dd19a76eaebe26d29d73132c2fa60";

	// A C7 game-data JSON save written before the movement scale existed, so its
	// terrain-improvement records still carry the old fractional road cost
	// (0.33333334). Tests that need a pre-change save ask for it here.
	private const string PreChangeGameDataSaveName = "Conquests 16 Players.json";
	private const string PreChangeGameDataSaveMd5 = "306c342992bfcfd502bfafd6a8cccf09";
	private const string PreChangeGameDataSaveUri =
		"https://www.dropbox.com/scl/fi/g1qxuvc6xptg1l6hx9s21/Conquests-16-Players.json?rlkey=bkq158od7469pibhtw44g04if&st=tqax1064&dl=1";

	/// <summary>
	/// Ensures 12345.SAV is present, downloading it when it is missing or does not
	/// match the pinned hash. Returns its path, or null when the download failed —
	/// callers should skip with a reason rather than fail, so an offline run
	/// reports why instead of appearing to pass.
	/// </summary>
	public static async Task<string> TryEnsureSampleSave() {
		string savesPath = PathUtils.getDataPath("saves");
		Directory.CreateDirectory(savesPath);

		string savePath = Path.Combine(savesPath, SampleSaveName);
		if (GetMd5FileHash(savePath) == SampleSaveMd5) {
			return savePath;
		}

		try {
			using HttpClient client = new();
			byte[] fileData = await client.GetByteArrayAsync($"https://drive.usercontent.google.com/download?id={DriveId}&confirm=y");
			await File.WriteAllBytesAsync(savePath, fileData);
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException) {
			return null;
		}

		return GetMd5FileHash(savePath) == SampleSaveMd5 ? savePath : null;
	}

	/// <summary>
	/// Ensures the pre-change game-data save fixture is present, downloading it
	/// when it is missing or does not match the pinned hash. Returns its path, or
	/// null when the download failed or the file is absent - callers should skip
	/// with a reason rather than fail, so an offline run reports why instead of
	/// appearing to pass.
	/// </summary>
	public static async Task<string> TryEnsurePreChangeGameDataSave() {
		string savesPath = PathUtils.getDataPath(Path.Combine("saves", "game-data"));
		Directory.CreateDirectory(savesPath);

		string savePath = Path.Combine(savesPath, PreChangeGameDataSaveName);
		if (GetMd5FileHash(savePath) == PreChangeGameDataSaveMd5) {
			return savePath;
		}

		try {
			using HttpClient client = new();
			byte[] fileData = await client.GetByteArrayAsync(PreChangeGameDataSaveUri);
			await File.WriteAllBytesAsync(savePath, fileData);
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException) {
			return null;
		}

		return GetMd5FileHash(savePath) == PreChangeGameDataSaveMd5 ? savePath : null;
	}

	public static string GetMd5FileHash(string path) {
		if (!File.Exists(path)) {
			return "";
		}

		using MD5 md5 = MD5.Create();
		using FileStream fileStream = File.OpenRead(path);
		byte[] hashBytes = md5.ComputeHash(fileStream);
		return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
	}
}
