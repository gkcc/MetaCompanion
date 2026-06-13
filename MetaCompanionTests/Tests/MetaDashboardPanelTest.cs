using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;

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

			Assert.AreEqual(2, panel.EnvironmentChartPanel.Children.Count);
			Assert.AreEqual(3, panel.EnvironmentListPanel.Children.Count);
			var mageRow = panel.EnvironmentChartPanel.Children
				.OfType<StackPanel>()
				.Single(row => row.ToolTip.ToString().StartsWith("\u6cd5\u5e08", StringComparison.Ordinal));
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
	}
}
