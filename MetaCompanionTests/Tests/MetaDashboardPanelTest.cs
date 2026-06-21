using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class MetaDashboardPanelTest
	{
		private string _tempDirectory;

		[TestInitialize]
		public void Initialize()
		{
			_tempDirectory = Path.Combine(
				Path.GetTempPath(), "MetaCompanionTests", Guid.NewGuid().ToString("N"));
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
		public void Update_WithEmptySnapshot_ShowsEmptyEnvironmentState()
		{
			var panel = new MetaDashboardPanel(null);

			panel.Update("title", new MetaDashboardSnapshot());

			Assert.AreEqual(1, panel.EnvironmentChartPanel.Children.Count);
			var empty = panel.EnvironmentChartPanel.Children[0] as TextBlock;
			Assert.IsNotNull(empty);
			StringAssert.Contains(empty.Text, "\u6682\u65e0");
		}

		[TestMethod]
		public void Update_RendersClassRowsAndArchetypeSegments()
		{
			WriteEnvironmentRows(
				"1\t142\t\u4efb\u52a1\u6cd5\tMAGE\t2\t2\t20\t98\t2\t0\t100",
				"2\t594\t\u6253\u8138\u6cd5\tMAGE\t1\t1\t10\t94\t0\t1\t0",
				"3\t56\t\u4efb\u52a1\u7267\tPRIEST\t4\t4\t45\t95\t3\t1\t75");
			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			var panel = new MetaDashboardPanel(null);

			panel.Update("title", snapshot);

			Assert.AreEqual(3, panel.EnvironmentChartPanel.Children.Count);
			Assert.AreEqual(3, panel.EnvironmentListPanel.Children.Count);
			var sectionTitles = ((StackPanel)panel.Child).Children
				.OfType<TextBlock>()
				.Select(text => text.Text)
				.ToList();
			CollectionAssert.Contains(sectionTitles, "\u8fd1\u671f\u5bf9\u624b\uff1a\u804c\u4e1a\u5408\u8ba1");
			CollectionAssert.Contains(sectionTitles, "\u8fd1\u671f\u6d41\u6d3e\uff1a\u5355\u9879\u6392\u884c");
			var subtitle = ((StackPanel)panel.Child).Children
				.OfType<TextBlock>()
				.Single(text => text.Text.Contains("\u6837\u672c"));
			StringAssert.Contains(subtitle.Text, "\u6837\u672c 7\u5c40");
			StringAssert.Contains(subtitle.ToolTip.ToString(), "\u804c\u4e1a\u662f\u5408\u8ba1");
			var overview = panel.EnvironmentChartPanel.Children[0] as Grid;
			Assert.IsNotNull(overview);
			Assert.AreEqual(10.0, overview.Height, 0.1);
			StringAssert.Contains(overview.ToolTip.ToString(), "\u5168\u6837\u672c\u804c\u4e1a\u5360\u6bd4");
			var mageRow = panel.EnvironmentChartPanel.Children
				.OfType<StackPanel>()
				.Single(row => row.ToolTip.ToString().Contains("\u804c\u4e1a\u5408\u8ba1\uff1a\u6cd5\u5e08"));
			var host = mageRow.Children
				.OfType<Grid>()
				.Single(child => Math.Abs(child.Height - 16.0) < 0.1);
			var active = host.Children.OfType<Grid>().Single();
			Assert.IsTrue(active.Width < host.Width);
			Assert.AreEqual(2, active.Children.OfType<Border>().Count());
			var firstSegment = active.Children.OfType<Border>().First();
			StringAssert.Contains(firstSegment.ToolTip.ToString(), "\u4efb\u52a1\u6cd5");
			StringAssert.Contains(firstSegment.ToolTip.ToString(), "\u804c\u4e1a\u5185\u9891\u6b21");
			StringAssert.Contains(firstSegment.ToolTip.ToString(), "2 \u5c40");
			var legend = mageRow.Children.OfType<WrapPanel>().Single();
			var firstLegendBadge = legend.Children
				.OfType<StackPanel>()
				.First()
				.Children
				.OfType<Border>()
				.Single()
				.Child as TextBlock;
			Assert.IsNotNull(firstLegendBadge);
			Assert.AreEqual("1", firstLegendBadge.Text);
			var legendText = string.Join(" ", legend.Children
				.OfType<StackPanel>()
				.SelectMany(item => item.Children.OfType<TextBlock>())
				.Select(text => text.Text));
			StringAssert.Contains(legendText, "\u4efb\u52a1\u6cd5");
			StringAssert.Contains(legendText, "\u6253\u8138\u6cd5");
		}

		[TestMethod]
		public void Update_SubtitleShowsRemoteSourceWhenAvailable()
		{
			WriteEnvironmentRows(
				"1\t56\t\u4efb\u52a1\u7267\tPRIEST\t4\t4\t45\t95\t3\t1\t75");
			WriteRemoteSource(
				"CURRENT_PATCH",
				"CURRENT_PATCH",
				40986,
				18765);
			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			var panel = new MetaDashboardPanel(null);

			panel.Update("title", snapshot);

			var subtitle = ((StackPanel)panel.Child).Children
				.OfType<TextBlock>()
				.Single(text => text.Text.Contains("\u8fdc\u7a0b"));
			StringAssert.Contains(subtitle.Text, "\u8fdc\u7a0b");
			StringAssert.Contains(subtitle.Text, "35.6.2\u8865\u4e01\u540e");
			StringAssert.Contains(subtitle.ToolTip.ToString(), "HSReplay \u8fdc\u7a0b\u6570\u636e\u6e90");
			StringAssert.Contains(subtitle.ToolTip.ToString(), "35.6.2\u8865\u4e01\u540e");
		}

		[TestMethod]
		public void Update_RendersLastGameExplanationAndLowConfidenceWarning()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\tcandidate_archetypes\tkey_evidence_cards" +
				Environment.NewLine +
				"g1\tWin\tRogue\t\u704c\u6ce8\u8d3c\t39\t" +
				"\u704c\u6ce8\u8d3c:39% score=120 branchCount=1 / \u6d77\u76d7\u8d3c:31% score=95 branchCount=2\t" +
				"\u8ff7\u4f60\u5305,\u9634\u5f71\u6b65" + Environment.NewLine,
				Encoding.UTF8);
			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			var panel = new MetaDashboardPanel(null);

			panel.Update("title", snapshot);

			var text = string.Join("\n", panel.LastGamePanel.Children
				.OfType<TextBlock>()
				.Select(block => block.Text));
			StringAssert.Contains(text, "\u704c\u6ce8\u8d3c");
			StringAssert.Contains(text, "39% score 120 branchCount 1");
			StringAssert.Contains(text, "\u4f4e\u7f6e\u4fe1\uff0c\u4ec5\u4f9b\u53c2\u8003");
			StringAssert.Contains(text, "\u8ff7\u4f60\u5305");
		}

		[TestMethod]
		public void GetClassColor_UsesWarcraftClassPaletteForAllHearthstoneClasses()
		{
			AssertColor("DEATHKNIGHT", 0xC4, 0x1E, 0x3A);
			AssertColor("DEMONHUNTER", 0xA3, 0x30, 0xC9);
			AssertColor("DRUID", 0xFF, 0x7C, 0x0A);
			AssertColor("EVOKER", 0x33, 0x93, 0x7F);
			AssertColor("HUNTER", 0xAA, 0xD3, 0x72);
			AssertColor("MAGE", 0x3F, 0xC7, 0xEB);
			AssertColor("MONK", 0x00, 0xFF, 0x98);
			AssertColor("PALADIN", 0xF4, 0x8C, 0xBA);
			AssertColor("PRIEST", 0xFF, 0xFF, 0xFF);
			AssertColor("ROGUE", 0xFF, 0xF4, 0x68);
			AssertColor("SHAMAN", 0x00, 0x70, 0xDD);
			AssertColor("WARLOCK", 0x87, 0x88, 0xEE);
			AssertColor("WARRIOR", 0xC6, 0x9B, 0x6D);
			Assert.AreEqual(
				MetaDashboardPanel.GetClassColor("DEMONHUNTER"),
				MetaDashboardPanel.GetClassColor("Demon Hunter"));
			Assert.AreEqual(
				MetaDashboardPanel.GetClassColor("DEMONHUNTER"),
				MetaDashboardPanel.GetClassColor("DEMON_HUNTER"));
		}

		private static void AssertColor(string playerClass, byte red, byte green, byte blue)
		{
			var color = MetaDashboardPanel.GetClassColor(playerClass);
			Assert.AreEqual(Color.FromRgb(red, green, blue), color, playerClass);
			Assert.AreEqual(color, MetaDashboardPanel.GetSegmentColor(playerClass, 0), playerClass);
		}

		private void WriteEnvironmentRows(params string[] rows)
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_environment.tsv"),
				"rank\tarchetype_id\tname\tplayer_class\tgames\tweighted_games\tlocal_pct\tavg_confidence\twins\tlosses\twin_rate" +
				Environment.NewLine +
				string.Join(Environment.NewLine, rows) +
				Environment.NewLine,
				Encoding.UTF8);
		}

		private void WriteRemoteSource(
			string summaryTimeRange,
			string selectedTimeRange,
			int currentPatchGames,
			int last3DaysGames)
		{
			var directory = Path.Combine(_tempDirectory, "Premium", "Meta", "latest");
			Directory.CreateDirectory(directory);
			File.WriteAllText(
				Path.Combine(directory, "summary.json"),
				"{" +
				"\"generated_at\":\"2026-06-13T00:42:46+08:00\"," +
				"\"as_of\":\"2026-06-12T09:21:35Z\"," +
				"\"time_range\":\"" + summaryTimeRange + "\"," +
				"\"patch_version\":\"35.6.2\"," +
				"\"game_type\":\"RANKED_STANDARD\"," +
				"\"rank_range\":\"DIAMOND_THROUGH_LEGEND\"," +
				"\"region\":\"ALL\"" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(directory, "manifest.json"),
				"{" +
				"\"selected_time_range\":\"" + selectedTimeRange + "\"," +
				"\"patch_version\":\"35.6.2\"" +
				"}",
				Encoding.UTF8);
		}
	}
}
