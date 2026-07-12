using MetaCompanion;
using Hearthstone_Deck_Tracker.Hearthstone;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class MetaRetrieverTest
	{
		private const string HeraldDeathKnightDeckCode =
			"AAECAfHhBAqSgwfDgweDigeCmAf0qgeosQfisQfQvwfqyQeb1AcKh/YE1J4G2OUGgf0Gl4IHupUHkasHj74HmsUH0MUHAAA=";

		[TestInitialize]
		public void Initialize()
		{
			HearthDb.Cards.LoadBaseData();
		}

		[TestMethod]
		public void ConvertDeckCode_UsesHearthDbCardsByDbfId()
		{
			var unknownDbfIds = new Dictionary<int, int>();
			var deck = MetaRetriever.ConvertDeckCode(
				HeraldDeathKnightDeckCode,
				"Herald Death Knight",
				unknownDbfIds);

			Assert.AreEqual("Herald Death Knight", deck.Name);
			Assert.AreEqual("Death Knight", deck.Class);
			Assert.AreEqual(0, unknownDbfIds.Count);
			Assert.IsTrue(deck.Cards.Any(card => card.Id == "RLK_708"));
		}

		[TestMethod]
		public void InferClass_UsesCorrectBundledHeroDbfIds()
		{
			Assert.AreEqual("Hunter", MetaRetriever.InferClass(31, new List<Card>()));
			Assert.AreEqual("Druid", MetaRetriever.InferClass(274, new List<Card>()));
			Assert.AreEqual("Mage", MetaRetriever.InferClass(637, new List<Card>()));
			Assert.AreEqual("Priest", MetaRetriever.InferClass(813, new List<Card>()));
		}

		[TestMethod]
		public void ParseDeckCodeEntry_KeepsOptionalDeckName()
		{
			var entry = MetaRetriever.ParseDeckCodeEntry("Quest Priest | " + HeraldDeathKnightDeckCode);

			Assert.AreEqual("Quest Priest", entry.Name);
			Assert.AreEqual(HeraldDeathKnightDeckCode, entry.Code);
		}

		[TestMethod]
		public void ParseDeckCodeEntry_IgnoresHsReplayDeckIdsThatLookLikeDeckCodes()
		{
			var entry = MetaRetriever.ParseDeckCodeEntry(
				"HSReplay deck\tAA0iwEekbHmF238T5wJtvFbAzwKLiYPsypLM2HpzTeW5sTI8Iz6uxvDtl0twoOz4hL\tCfFnh5jtDmuU7WFjdgzsc");

			Assert.IsNull(entry);
		}

		[TestMethod]
		public void SelectDeckCodeFilePaths_PrefersCurrentPatchBranchOverUnscopedHsReplay()
		{
			WithTempDirectory(tempDirectory =>
			{
				var manualPath = Path.Combine(tempDirectory, "deckcodes.txt");
				var branchPath = Path.Combine(tempDirectory, "archetype_deck_branches.tsv");
				var hsReplayPath = Path.Combine(tempDirectory, "hsreplay_deckcodes.txt");
				var hsGuruPath = Path.Combine(tempDirectory, "hsguru_deckcodes.txt");
				File.WriteAllText(Path.Combine(tempDirectory, "patch_marker.txt"), "2026-07-07T19:16:55+08:00");
				File.WriteAllText(branchPath,
					"# CandidateAsOf: 2026-07-08T07:58:37Z\n" +
					"# CandidateTimeRange: CURRENT_PATCH\n");

				var selected = MetaRetriever.SelectDeckCodeFilePaths(new[]
				{
					manualPath,
					branchPath,
					hsReplayPath,
					hsGuruPath
				}, tempDirectory);

				CollectionAssert.AreEqual(new[] {manualPath, branchPath}, selected);
			});
		}

		[TestMethod]
		public void SelectDeckCodeFilePaths_PrefersHsReplayOverNonCurrentBranchFallback()
		{
			WithTempDirectory(tempDirectory =>
			{
				var manualPath = Path.Combine(tempDirectory, "deckcodes.txt");
				var branchPath = Path.Combine(tempDirectory, "archetype_deck_branches.tsv");
				var hsReplayPath = Path.Combine(tempDirectory, "hsreplay_deckcodes.txt");
				File.WriteAllText(branchPath, "# CandidateTimeRange: LAST_7_DAYS\n");

				var selected = MetaRetriever.SelectDeckCodeFilePaths(new[]
				{
					manualPath,
					branchPath,
					hsReplayPath
				}, tempDirectory);

				CollectionAssert.AreEqual(new[] {manualPath, hsReplayPath}, selected);
			});
		}

		[TestMethod]
		public void SelectDeckCodeFilePaths_FallsBackToHsGuruAndBranch()
		{
			var tempDirectory = Path.Combine(Path.GetTempPath(), "MetaCompanionTests", Path.GetRandomFileName());
			Directory.CreateDirectory(tempDirectory);
			try
			{
				var branchPath = Path.Combine(tempDirectory, "archetype_deck_branches.tsv");
				var hsReplayPath = Path.Combine(tempDirectory, "hsreplay_deckcodes.txt");
				var hsGuruPath = Path.Combine(tempDirectory, "hsguru_deckcodes.txt");

				CollectionAssert.AreEqual(
					new[] {hsReplayPath},
					MetaRetriever.SelectDeckCodeFilePaths(new[] {hsReplayPath, hsGuruPath}, tempDirectory));
				CollectionAssert.AreEqual(
					new[] {hsGuruPath, branchPath},
					MetaRetriever.SelectDeckCodeFilePaths(new[] {hsGuruPath, branchPath}, tempDirectory));
				CollectionAssert.AreEqual(
					new[] {hsGuruPath},
					MetaRetriever.SelectDeckCodeFilePaths(new[] {hsGuruPath}, tempDirectory));
				CollectionAssert.AreEqual(
					new[] {branchPath},
					MetaRetriever.SelectDeckCodeFilePaths(new[] {branchPath}, tempDirectory));
			}
			finally
			{
				if (Directory.Exists(tempDirectory))
				{
					Directory.Delete(tempDirectory, true);
				}
			}
		}

		[TestMethod]
		public void SelectDeckCodeFilePaths_ReturnsEmptyForCleanInstall()
		{
			var selected = MetaRetriever.SelectDeckCodeFilePaths(new string[0]);

			Assert.AreEqual(0, selected.Count);
		}

		[TestMethod]
		public void LoadDeckCodeDecks_FallsBackWhenHsReplaySnapshotHasNoValidDeckStrings()
		{
			var tempDirectory = Path.Combine(Path.GetTempPath(), "MetaCompanionTests", Path.GetRandomFileName());
			Directory.CreateDirectory(tempDirectory);
			try
			{
				File.WriteAllText(
					Path.Combine(tempDirectory, "hsreplay_deckcodes.txt"),
					"HSReplay deck\tAA0iwEekbHmF238T5wJtvFbAzwKLiYPsypLM2HpzTeW5sTI8Iz6uxvDtl0twoOz4hL");
				File.WriteAllText(
					Path.Combine(tempDirectory, "archetype_deck_branches.tsv"),
					"Herald Death Knight\t" + HeraldDeathKnightDeckCode);

				var decks = MetaRetriever.LoadDeckCodeDecks(tempDirectory);

				Assert.AreEqual(1, decks.Count);
				Assert.AreEqual("Herald Death Knight", decks[0].Name);
				Assert.AreEqual("Death Knight", decks[0].Class);
			}
			finally
			{
				if (Directory.Exists(tempDirectory))
				{
					Directory.Delete(tempDirectory, true);
				}
			}
		}

		private static void WithTempDirectory(System.Action<string> action)
		{
			var tempDirectory = Path.Combine(Path.GetTempPath(), "MetaCompanionTests", Path.GetRandomFileName());
			Directory.CreateDirectory(tempDirectory);
			try
			{
				action(tempDirectory);
			}
			finally
			{
				if (Directory.Exists(tempDirectory))
				{
					Directory.Delete(tempDirectory, true);
				}
			}
		}
	}
}
