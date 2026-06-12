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
		public void SelectDeckCodeFilePaths_PrefersHsReplayOverBranchFallback()
		{
			var manualPath = DataPath("deckcodes.txt");
			var branchPath = DataPath("archetype_deck_branches.tsv");
			var hsReplayPath = DataPath("hsreplay_deckcodes.txt");
			var hsGuruPath = DataPath("hsguru_deckcodes.txt");

			var selected = MetaRetriever.SelectDeckCodeFilePaths(new[]
				{
					manualPath,
					branchPath,
					hsReplayPath,
					hsGuruPath
				});

			CollectionAssert.AreEqual(new[] {manualPath, hsReplayPath}, selected);
		}

		[TestMethod]
		public void SelectDeckCodeFilePaths_FallsBackToHsGuruThenBranch()
		{
			var branchPath = DataPath("archetype_deck_branches.tsv");
			var hsReplayPath = DataPath("hsreplay_deckcodes.txt");
			var hsGuruPath = DataPath("hsguru_deckcodes.txt");

			CollectionAssert.AreEqual(
				new[] {hsReplayPath},
				MetaRetriever.SelectDeckCodeFilePaths(new[] {hsReplayPath, hsGuruPath}));
			CollectionAssert.AreEqual(
				new[] {hsGuruPath},
				MetaRetriever.SelectDeckCodeFilePaths(new[] {hsGuruPath}));
			CollectionAssert.AreEqual(
				new[] {branchPath},
				MetaRetriever.SelectDeckCodeFilePaths(new[] {branchPath}));
		}

		private static string DataPath(string fileName)
		{
			return Path.Combine(MetaCompanionPlugin.DataDirectory, fileName);
		}
	}
}
