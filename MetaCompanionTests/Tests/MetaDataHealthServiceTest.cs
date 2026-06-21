using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class MetaDataHealthServiceTest
	{
		private readonly DateTime _now = new DateTime(2026, 6, 22, 9, 0, 0);
		private string _tempDirectory;

		[TestInitialize]
		public void Initialize()
		{
			_tempDirectory = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionTests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_tempDirectory);
		}

		[TestCleanup]
		public void Cleanup()
		{
			if (Directory.Exists(_tempDirectory))
			{
				Directory.Delete(_tempDirectory, true);
			}
		}

		[TestMethod]
		public void Inspect_EmptyDirectory_ReturnsEmptySnapshot()
		{
			var snapshot = Inspect();

			Assert.AreEqual(MetaDataHealthOverallStatus.Empty, snapshot.OverallStatus);
			Assert.IsFalse(snapshot.PredictionAvailable);
			Assert.IsFalse(snapshot.RecommendationAvailable);
			Assert.AreEqual(
				"\u5c1a\u672a\u751f\u6210\u6570\u636e\u5feb\u7167",
				snapshot.UserMessage);
		}

		[TestMethod]
		public void Inspect_PublicOnlyDeckSnapshot_EnablesPredictionOnly()
		{
			WriteDeckSnapshot();

			var snapshot = Inspect();

			Assert.AreEqual(MetaDataHealthOverallStatus.Partial, snapshot.OverallStatus);
			Assert.IsTrue(snapshot.PredictionAvailable);
			Assert.IsFalse(snapshot.RecommendationAvailable);
			Assert.AreEqual(
				"\u5bf9\u5c40\u9884\u6d4b\u53ef\u7528\uff0c\u63a8\u8350\u6570\u636e\u672a\u751f\u6210",
				snapshot.UserMessage);
			StringAssert.Contains(Details(snapshot), "HSReplay \u724c\u7ec4\u5e93: 1 \u5957");
			StringAssert.Contains(Details(snapshot), "\u4e2a\u4eba\u63a8\u8350: \u672a\u751f\u6210");
		}

		[TestMethod]
		public void Inspect_FullPremiumData_ShowsRemoteRecommendationLocalAndCookieState()
		{
			WriteFullPremiumData();
			WriteTools();
			WriteFile("hsreplay_cookie.txt", "secret-cookie-value");
			WriteFile(
				"local_meta_environment.tsv",
				"rank\tarchetype_id\tname\tplayer_class\tgames" + Environment.NewLine +
				"1\t56\tQuest Priest\tPRIEST\t4" + Environment.NewLine +
				"2\t857\tHerald Shaman\tSHAMAN\t3" + Environment.NewLine);

			var snapshot = Inspect();
			var details = Details(snapshot);

			Assert.AreEqual(MetaDataHealthOverallStatus.Ready, snapshot.OverallStatus);
			Assert.IsTrue(snapshot.PredictionAvailable);
			Assert.IsTrue(snapshot.PremiumAvailable);
			Assert.IsTrue(snapshot.RecommendationAvailable);
			Assert.IsTrue(snapshot.LocalHistoryAvailable);
			Assert.AreEqual(
				"\u5bf9\u5c40\u9884\u6d4b\u4e0e\u63a8\u8350\u6570\u636e\u53ef\u7528",
				snapshot.UserMessage);
			StringAssert.Contains(details, "\u8fdc\u7a0b\u65f6\u95f4\u8303\u56f4: CURRENT_PATCH");
			StringAssert.Contains(details, "\u8865\u4e01\u7248\u672c: 35.6.2");
			StringAssert.Contains(details, "\u4e2a\u4eba\u63a8\u8350: 2 \u884c");
			StringAssert.Contains(details, "\u672c\u5730\u73af\u5883\u6837\u672c: 7 \u5c40");
			StringAssert.Contains(details, "Premium Cookie \u5df2\u914d\u7f6e");
			Assert.IsFalse(details.Contains("secret-cookie-value"));
		}

		[TestMethod]
		public void Inspect_CorruptedManifestJson_ReturnsErrorSnapshot()
		{
			WriteDeckSnapshot();
			WriteFile("Premium\\Meta\\latest\\summary.json", "{}");
			WriteFile("Premium\\Meta\\latest\\head_to_head_archetype_matchups_v2.json", "{}");
			WriteFile("Premium\\Meta\\latest\\manifest.json", "{");

			var snapshot = Inspect();

			Assert.AreEqual(MetaDataHealthOverallStatus.Error, snapshot.OverallStatus);
			Assert.IsFalse(snapshot.PredictionAvailable);
			StringAssert.Contains(
				snapshot.UserMessage,
				"\u6570\u636e\u5065\u5eb7\u68c0\u67e5\u5931\u8d25");
		}

		[TestMethod]
		public void Inspect_MissingTools_DowngradesOtherwiseReadySnapshotToPartial()
		{
			WriteFullPremiumData();

			var snapshot = Inspect();
			var details = Details(snapshot);

			Assert.AreEqual(MetaDataHealthOverallStatus.Partial, snapshot.OverallStatus);
			Assert.IsTrue(snapshot.PredictionAvailable);
			Assert.IsTrue(snapshot.PremiumAvailable);
			Assert.IsTrue(snapshot.RecommendationAvailable);
			Assert.AreEqual(
				"\u5bf9\u5c40\u9884\u6d4b\u4e0e\u63a8\u8350\u6570\u636e\u53ef\u7528\uff0c\u5237\u65b0\u811a\u672c\u7f3a\u5931",
				snapshot.UserMessage);
			StringAssert.Contains(details, "\u5237\u65b0\u811a\u672c\u7f3a\u5931");
			StringAssert.Contains(details, "Update-MetaCompanionData.ps1");
			StringAssert.Contains(details, "Run-MetaCompanionRefresh.ps1");
		}

		[TestMethod]
		public void Inspect_CommentOnlyCookieFile_IsNotConfigured()
		{
			WriteFullPremiumData();
			WriteTools();
			WriteFile(
				"hsreplay_cookie.txt",
				"# paste cookie below" + Environment.NewLine +
				"   " + Environment.NewLine);

			var snapshot = Inspect();

			StringAssert.Contains(Details(snapshot), "Premium Cookie \u672a\u914d\u7f6e");
		}

		private MetaDataHealthSnapshot Inspect()
		{
			return new MetaDataHealthService(
				_tempDirectory,
				_now,
				TimeSpan.FromHours(24)).Inspect();
		}

		private void WriteFullPremiumData()
		{
			WriteDeckSnapshot();
			WriteFile(
				"Premium\\Meta\\latest\\summary.json",
				"{\"generated_at\":\"2026-06-22T08:30:00+08:00\"}");
			WriteFile("Premium\\Meta\\latest\\head_to_head_archetype_matchups_v2.json", "{}");
			WriteFile(
				"Premium\\Meta\\latest\\manifest.json",
				"{\"selected_time_range\":\"CURRENT_PATCH\",\"patch_version\":\"35.6.2\"}");
			WriteFile(
				"Premium\\Meta\\latest\\personal_recommendations.tsv",
				"rank\tname\texpected_win_rate" + Environment.NewLine +
				"1\tQuest Priest\t58.1" + Environment.NewLine +
				"2\tHerald Shaman\t55.0" + Environment.NewLine);
		}

		private void WriteDeckSnapshot()
		{
			WriteFile(
				"hsreplay_deckcodes.txt",
				"# Count: 1" + Environment.NewLine +
				"Quest Priest | AAECAf0GAA==" + Environment.NewLine);
		}

		private void WriteTools()
		{
			WriteFile("Tools\\Update-MetaCompanionData.ps1", "");
			WriteFile("Tools\\Run-MetaCompanionRefresh.ps1", "");
		}

		private void WriteFile(string relativePath, string contents)
		{
			var path = Path.Combine(_tempDirectory, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, contents, Encoding.UTF8);
			File.SetLastWriteTime(path, _now.AddMinutes(-5));
		}

		private static string Details(MetaDataHealthSnapshot snapshot)
		{
			return string.Join("\n", snapshot.DetailLines.ToArray());
		}
	}
}
