using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
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
		public void Constructor_DoesNotAttachLastGameCorrectionLayout()
		{
			var panel = new MetaDashboardPanel(null);

			Assert.IsNull(panel.LastGamePanel.Parent);
			var visibleText = string.Join("\n", ((StackPanel)panel.Child).Children
				.OfType<TextBlock>()
				.Select(block => block.Text));
			Assert.IsFalse(visibleText.Contains("最近一局"));
		}

		[TestMethod]
		public void LocalSampleControls_AreVisibleOnDashboardAndDispatchAllThreeActions()
		{
			var calls = 0;
			var observedAction = LocalSampleActionKind.Clear;
			var observedDays = -1;
			var observedMatches = -1;
			var panel = new MetaDashboardPanel(null, null, (action, days, matches) =>
			{
				calls++;
				observedAction = action;
				observedDays = days;
				observedMatches = matches;
			});
			panel.SetLocalSampleState(3, 0, false, "", false);
			panel.Update("卡组流派推荐", new MetaDashboardSnapshot());

			Assert.IsNotNull(panel.LocalSampleExpander.Parent,
				"本地样本入口必须直接出现在流派推荐面板，而不是只藏在设置页。");
			var header = panel.LocalSampleExpander.Header as TextBlock;
			Assert.IsNotNull(header);
			StringAssert.Contains(header.Text, "最近 3 天");
			StringAssert.Contains(header.Text, "不限场数");

			panel.LocalSampleDayPresetButtons[7]
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			panel.LocalSampleMatchPresetButtons[20]
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			panel.ApplyLocalSampleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.AreEqual(1, calls);
			Assert.AreEqual(LocalSampleActionKind.ApplyFilters, observedAction);
			Assert.AreEqual(7, observedDays);
			Assert.AreEqual(20, observedMatches);

			panel.ClearLocalSampleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.AreEqual(2, calls);
			Assert.AreEqual(LocalSampleActionKind.Clear, observedAction);

			panel.RestoreLocalSampleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.AreEqual(3, calls);
			Assert.AreEqual(LocalSampleActionKind.RestoreCurrentPatch, observedAction);
		}

		[TestMethod]
		public void LocalSampleControls_UseMousePresetsWithoutOverlayTextBoxes()
		{
			var panel = new MetaDashboardPanel(null, null, (action, days, matches) => { });
			panel.SetLocalSampleState(3, 0, false, "", false);

			CollectionAssert.AreEquivalent(
				new[] { 0, 1, 3, 7, 14, 30 },
				panel.LocalSampleDayPresetButtons.Keys.ToArray());
			CollectionAssert.AreEquivalent(
				new[] { 0, 10, 20, 50, 100 },
				panel.LocalSampleMatchPresetButtons.Keys.ToArray());
			var content = panel.LocalSampleExpander.Content as Border;
			Assert.IsNotNull(content);
			var body = content.Child as StackPanel;
			Assert.IsNotNull(body);
			Assert.AreEqual(0, body.Children
				.OfType<WrapPanel>()
				.SelectMany(row => row.Children.OfType<TextBox>())
				.Count(), "HDT 不激活浮窗中不能再放依赖键盘焦点的输入框。");

			panel.LocalSampleDayPresetButtons[14]
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			panel.LocalSampleMatchPresetButtons[50]
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			StringAssert.Contains(panel.LocalSampleActionStatus.Text, "最近 14 天");
			StringAssert.Contains(panel.LocalSampleActionStatus.Text, "最近 50 场");
			StringAssert.Contains(panel.LocalSampleActionStatus.Text, "应用筛选");
		}

		[TestMethod]
		public void LocalSampleControls_VisibleDashboardPollPreservesPendingPreset()
		{
			var panel = new MetaDashboardPanel(null, null, (action, days, matches) => { });
			panel.SetLocalSampleState(3, 0, false, "本地样本筛选已应用。", false);
			panel.Visibility = Visibility.Visible;

			panel.LocalSampleDayPresetButtons[0]
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			Assert.IsTrue(panel.HasPendingLocalSampleSelection);
			Assert.IsFalse(MetaDashboardView.ShouldSyncLocalSamplePanelState(panel),
				"每秒一次的可见面板刷新不得用已应用配置覆盖尚未应用的筛选草稿。");
			var header = panel.LocalSampleExpander.Header as TextBlock;
			Assert.IsNotNull(header);
			StringAssert.Contains(header.Text, "本补丁全部天数");
			StringAssert.Contains(panel.LocalSampleActionStatus.Text, "已选择");

			panel.Visibility = Visibility.Collapsed;
			Assert.IsTrue(MetaDashboardView.ShouldSyncLocalSamplePanelState(panel),
				"面板重新打开时应从已应用配置重新同步状态。");
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
			var status = ((StackPanel)panel.Child).Children
				.OfType<TextBlock>()
				.Single(text => text.Text.StartsWith("\u63d0\u793a\uff1a"));
			StringAssert.Contains(status.Text, "\u8bf7\u5148\u8fd0\u884c\u4e00\u6b21\u6570\u636e\u66f4\u65b0");
		}

		[TestMethod]
		public void Update_WithReadIssue_ShowsSafeChineseActionAndPartialData()
		{
			var recommendationDirectory = Path.Combine(
				_tempDirectory, "Premium", "Meta", "latest");
			Directory.CreateDirectory(recommendationDirectory);
			var recommendationPath = Path.Combine(
				recommendationDirectory, "personal_recommendations.tsv");
			File.WriteAllText(
				recommendationPath,
				"rank\tname\tplayer_class" + Environment.NewLine +
				"1\t\u5143\u7d20\u8428\tSHAMAN" + Environment.NewLine,
				Encoding.UTF8);
			WriteEnvironmentRows(
				"1\t56\t\u4efb\u52a1\u7267\tPRIEST\t4\t4\t45\t95\t3\t1\t75");

			MetaDashboardSnapshot snapshot;
			using (new FileStream(
				recommendationPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.None))
			{
				snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			}
			var panel = new MetaDashboardPanel(null);

			panel.Update("\u5361\u7ec4\u6d41\u6d3e\u63a8\u8350", snapshot);

			var status = ((StackPanel)panel.Child).Children
				.OfType<TextBlock>()
				.Single(text => text.Text.StartsWith("\u9700\u5904\u7406\uff1a"));
			StringAssert.Contains(
				status.Text,
				"\u8bf7\u5728\u8bbe\u7f6e\u9875\u5237\u65b0\u6216\u91cd\u65b0\u751f\u6210\u6570\u636e");
			Assert.AreEqual(1, panel.EnvironmentListPanel.Children.Count,
				"\u8bfb\u53d6\u6545\u969c\u4e0d\u5e94\u9690\u85cf\u5176\u4ed6\u5df2\u52a0\u8f7d\u6570\u636e\u3002");
			Assert.IsFalse(status.Text.Contains("IOException"));
			Assert.IsFalse(status.Text.Contains("Exception"));
			Assert.IsFalse(status.Text.Contains("Error"));
			Assert.IsFalse(status.Text.Contains("failed"));
			Assert.IsFalse(status.Text.Contains(_tempDirectory));
		}

		[TestMethod]
		public void Update_WithNullSnapshot_ShowsSafeChineseFailureState()
		{
			var panel = new MetaDashboardPanel(null);

			panel.Update("\u5361\u7ec4\u6d41\u6d3e\u63a8\u8350", null);

			var status = ((StackPanel)panel.Child).Children
				.OfType<TextBlock>()
				.Single(text => text.Text.StartsWith("\u9700\u5904\u7406\uff1a"));
			StringAssert.Contains(status.Text, "\u8bf7\u5728\u8bbe\u7f6e\u9875\u5237\u65b0");
			Assert.IsFalse(status.Text.Contains("Exception"));
		}

		[TestMethod]
		public void Update_RecommendationsWithoutSameScopeDeckCodesShowsShortChineseNotice()
		{
			var recommendationDirectory = Path.Combine(
				_tempDirectory, "Premium", "Meta", "latest");
			Directory.CreateDirectory(recommendationDirectory);
			File.WriteAllText(
				Path.Combine(recommendationDirectory, "personal_recommendations.tsv"),
				"rank\tname\tplayer_class\texpected_win_rate" + Environment.NewLine +
				"1\t\u5146\u793a\u8428\tSHAMAN\t58.2" + Environment.NewLine,
				Encoding.UTF8);
			var panel = new MetaDashboardPanel(null);

			panel.Update("\u5361\u7ec4\u6d41\u6d3e\u63a8\u8350", MetaDashboardSnapshot.Load(_tempDirectory));

			var notice = panel.RecommendationsPanel.Children
				.OfType<TextBlock>()
				.Single();
			Assert.AreEqual("\u5f53\u524d\u53e3\u5f84\u6682\u65e0\u540c\u8303\u56f4\u5361\u7ec4\u4ee3\u7801", notice.Text);
			StringAssert.Contains(notice.ToolTip.ToString(), "\u63a8\u8350\u6392\u5e8f\u4ecd\u7136\u6709\u6548");
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
			StringAssert.Contains(subtitle.Text, Environment.NewLine + "\u8fdc\u7a0b");
			Assert.AreEqual(TextWrapping.Wrap, subtitle.TextWrapping);
			Assert.AreEqual(TextTrimming.None, subtitle.TextTrimming);
			StringAssert.Contains(subtitle.Text, "35.6.2\u8865\u4e01\u540e");
			StringAssert.Contains(subtitle.ToolTip.ToString(), "HSReplay \u8fdc\u7a0b\u6570\u636e\u6e90");
			StringAssert.Contains(subtitle.ToolTip.ToString(), "35.6.2\u8865\u4e01\u540e");
			StringAssert.Contains(subtitle.ToolTip.ToString(), "\u6807\u51c6\u6a21\u5f0f\uff08\u5929\u68af\uff09");
			StringAssert.Contains(subtitle.ToolTip.ToString(), "\u5168\u90e8\u5730\u533a");
			Assert.IsFalse(subtitle.ToolTip.ToString().Contains("CURRENT_PATCH"));
			Assert.IsFalse(subtitle.ToolTip.ToString().Contains("RANKED_STANDARD"));
			Assert.IsFalse(subtitle.ToolTip.ToString().Contains("ALL"));
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
			StringAssert.Contains(text, "39% / \u5339\u914d\u5206 120 / \u5206\u652f 1");
			Assert.IsFalse(text.Contains("score"));
			Assert.IsFalse(text.Contains("branchCount"));
			StringAssert.Contains(text, "\u4f4e\u7f6e\u4fe1\uff0c\u4ec5\u4f9b\u53c2\u8003");
			StringAssert.Contains(text, "\u8ff7\u4f60\u5305");
		}

		[TestMethod]
		public void Update_ExactUnknownPlaceholderNeverAppearsInVisibleLastGameText()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\t" +
					"candidate_archetypes" + Environment.NewLine +
				"g1\tunknown\tMage\tUnknown\t25\tUnknown:25% score=12 branchCount=1" +
					Environment.NewLine,
				Encoding.UTF8);
			var panel = new MetaDashboardPanel(null);

			panel.Update("title", MetaDashboardSnapshot.Load(_tempDirectory));

			var visibleText = string.Join("\n",
				panel.LastGamePanel.Children.OfType<TextBlock>().Select(block => block.Text)
					.Concat(panel.LastGamePanel.Children.OfType<WrapPanel>()
						.SelectMany(row => row.Children.OfType<TextBox>())
						.Select(box => box.Text))
					.Concat(panel.LastGamePanel.Children.OfType<WrapPanel>()
						.SelectMany(row => row.Children.OfType<Button>())
						.Select(button => Convert.ToString(button.Content))));
			StringAssert.Contains(visibleText, "\u672a\u8bc6\u522b\u6d41\u6d3e");
			Assert.IsFalse(visibleText.Contains("Unknown"));
			Assert.IsFalse(visibleText.Contains("unknown"));
		}

		[TestMethod]
		public void Update_CorrectionCandidateButtonsFillTextBoxAndSubmit()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\tcandidate_archetypes\tkey_evidence_cards" +
				Environment.NewLine +
				"g1\tWin\tRogue\t\u704c\u6ce8\u8d3c\t62\t" +
				"\u704c\u6ce8\u8d3c:62% score=120 branchCount=1 / \u6d77\u76d7\u8d3c:28% score=95 branchCount=2\t" +
				"\u8ff7\u4f60\u5305,\u9634\u5f71\u6b65" + Environment.NewLine,
				Encoding.UTF8);
			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			var correction = "";
			var panel = new MetaDashboardPanel(null, (matchId, archetype) =>
			{
				correction = matchId + ":" + archetype;
				return false;
			});

			panel.Update("title", snapshot);

			var input = panel.LastGamePanel.Children
				.OfType<WrapPanel>()
				.SelectMany(row => row.Children.OfType<TextBox>())
				.Single();
			Assert.AreEqual("\u704c\u6ce8\u8d3c", input.Text);
			Assert.AreEqual(0, panel.LastGamePanel.Children
				.OfType<WrapPanel>()
				.SelectMany(row => row.Children.OfType<ComboBox>())
				.Count());
			var buttons = panel.LastGamePanel.Children
				.OfType<WrapPanel>()
				.SelectMany(row => row.Children.OfType<Button>())
				.ToList();
			buttons.Single(button => Convert.ToString(button.Content) == "\u6d77\u76d7\u8d3c")
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Assert.AreEqual("\u6d77\u76d7\u8d3c", input.Text);

			buttons.Single(button => Convert.ToString(button.Content) == "\u4fee\u6b63\u672c\u5c40")
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			Assert.AreEqual("g1:\u6d77\u76d7\u8d3c", correction);
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
