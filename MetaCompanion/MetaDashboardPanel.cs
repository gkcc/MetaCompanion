using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MetaCompanion
{
	internal class MetaDashboardPanel : Border
	{
		private const double DistributionBarWidth = 318.0;
		private const double DistributionBarHeight = 16.0;
		private const double MinScaledBarWidth = 18.0;
		private const string DashboardToolTip =
			"\u4f20\u7edf\u5bf9\u6218\u5165\u53e3\u663e\u793a\u3002\u63a8\u8350\u6765\u81ea HSReplay \u5bf9\u9635\u80dc\u7387\u548c\u672c\u5730\u8fd1\u671f\u5bf9\u624b\u5206\u5e03\uff0c\u4e0d\u662f\u5f53\u524d\u5bf9\u624b\u5b9e\u65f6\u8bc6\u522b\u3002\u8fdb\u5165\u5b9e\u9645\u5bf9\u5c40\u540e\u4f1a\u81ea\u52a8\u9690\u85cf\u3002";
		private const string RecommendationsToolTip =
			"\u6d41\u6d3e\u63a8\u8350\u6309 HSReplay \u5bf9\u9635\u77e9\u9635\u80dc\u7387\u548c\u672c\u5730\u8fd1 3 \u5929\u5bf9\u624b\u5206\u5e03\u52a0\u6743\u6392\u5e8f\uff1b\u9ed8\u8ba4\u672c\u5730\u5206\u5e03\u6743\u91cd 35%\u3002";
		private const string EnvironmentToolTip =
			"\u8fd1\u671f\u5bf9\u624b\u6765\u81ea HDT \u672c\u5730\u5386\u53f2\uff0c\u6309\u539f\u59cb\u5c40\u6570\u7edf\u8ba1\u804c\u4e1a\u548c\u5f62\u6001\u9891\u6b21\u3002";

		private readonly Action _closeAction;
		private readonly TextBlock _title;
		private readonly TextBlock _subtitle;
		private readonly StackPanel _recommendations;
		private readonly StackPanel _environmentChart;
		private readonly StackPanel _environment;
		private readonly Grid _header;

		public MetaDashboardPanel(Action closeAction)
		{
			_closeAction = closeAction;
			Width = 368;
			Background = Brush("#EA171E27");
			BorderBrush = Brush("#806D7C8C");
			BorderThickness = new Thickness(1);
			CornerRadius = new CornerRadius(4);
			Padding = new Thickness(12);
			IsHitTestVisible = true;
			ToolTip = DashboardToolTip;

			var root = new StackPanel();
			Child = root;

			_header = new Grid { Cursor = Cursors.SizeAll, ToolTip = "\u62d6\u52a8\u8fd9\u91cc\u8c03\u6574\u4f4d\u7f6e" };
			_header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			_header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			root.Children.Add(_header);

			_title = new TextBlock
			{
				Foreground = Brush("#FFF4F7FA"),
				FontSize = 15,
				FontWeight = FontWeights.SemiBold,
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center,
				ToolTip = DashboardToolTip
			};
			Grid.SetColumn(_title, 0);
			_header.Children.Add(_title);

			var close = new Button
			{
				Content = "x",
				Width = 24,
				Height = 22,
				Padding = new Thickness(0),
				Margin = new Thickness(8, 0, 0, 0),
				Cursor = Cursors.Arrow,
				ToolTip = "\u5173\u95ed\u672c\u6b21\u63a8\u8350\u9762\u677f"
			};
			close.Click += (sender, args) => _closeAction?.Invoke();
			Grid.SetColumn(close, 1);
			_header.Children.Add(close);

			_subtitle = new TextBlock
			{
				Foreground = Brush("#FFA4B8CC"),
				FontSize = 11,
				Margin = new Thickness(0, 2, 0, 9),
				TextTrimming = TextTrimming.CharacterEllipsis,
				ToolTip = DashboardToolTip
			};
			root.Children.Add(_subtitle);

			root.Children.Add(SectionTitle("\u63a8\u8350\u6d41\u6d3e", RecommendationsToolTip));
			_recommendations = new StackPanel { Margin = new Thickness(0, 4, 0, 9) };
			root.Children.Add(_recommendations);

			root.Children.Add(SectionTitle("\u8fd1\u671f\u5bf9\u624b", EnvironmentToolTip));
			_environmentChart = new StackPanel { Margin = new Thickness(0, 5, 0, 6) };
			root.Children.Add(_environmentChart);
			_environment = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
			root.Children.Add(_environment);
		}

		public UIElement DragHandle => _header;
		internal StackPanel EnvironmentChartPanel => _environmentChart;
		internal StackPanel EnvironmentListPanel => _environment;

		public void Update(string title, MetaDashboardSnapshot snapshot)
		{
			_title.Text = title ?? "\u5361\u7ec4\u6d41\u6d3e\u63a8\u8350";
			_title.ToolTip = DashboardToolTip;
			if (snapshot == null || !snapshot.HasContent)
			{
				_subtitle.Text = "\u6682\u65e0\u672c\u5730\u63a8\u8350\u7f13\u5b58";
				FillItems(_recommendations, Enumerable.Empty<MetaDashboardItem>(),
					"\u5148\u8fd0\u884c\u4e00\u6b21\u6570\u636e\u66f4\u65b0\u540e\u663e\u793a\u63a8\u8350");
				FillEnvironmentChart(_environmentChart, Enumerable.Empty<MetaDashboardClassDistribution>());
				FillItems(_environment, Enumerable.Empty<MetaDashboardItem>(), "\u6682\u65e0\u8fd1\u671f\u5bf9\u624b\u5206\u5e03");
				return;
			}

			_subtitle.Text = snapshot.UpdatedAt.HasValue
				? "\u66f4\u65b0 " + snapshot.UpdatedAt.Value.ToString("MM-dd HH:mm")
				: "\u8bfb\u53d6\u672c\u5730\u7f13\u5b58";
			_subtitle.ToolTip = DashboardToolTip;
			FillItems(_recommendations, snapshot.Recommendations, "\u6682\u65e0\u63a8\u8350\u7ed3\u679c");
			FillEnvironmentChart(_environmentChart, snapshot.EnvironmentClasses);
			FillItems(_environment, snapshot.Environment, "\u6682\u65e0\u8fd1\u671f\u5bf9\u624b\u5206\u5e03");
		}

		private static TextBlock SectionTitle(string text, string toolTip)
		{
			return new TextBlock
			{
				Text = text,
				Foreground = Brush("#FF7DD3FC"),
				FontSize = 11,
				FontWeight = FontWeights.SemiBold,
				ToolTip = toolTip
			};
		}

		private static void FillEnvironmentChart(
			Panel target,
			IEnumerable<MetaDashboardClassDistribution> classes)
		{
			target.Children.Clear();
			var list = classes == null
				? new List<MetaDashboardClassDistribution>()
				: classes.ToList();
			if (list.Count == 0)
			{
				target.Children.Add(new TextBlock
				{
					Text = "\u6682\u65e0\u8fd1\u671f\u5bf9\u624b\u5206\u5e03",
					Foreground = Brush("#FF8FA1B2"),
					FontSize = 12,
					TextWrapping = TextWrapping.Wrap,
					ToolTip = EnvironmentToolTip
				});
				return;
			}

			var maxGames = list.Max(item => item.Games);
			for (var index = 0; index < list.Count; index++)
			{
				target.Children.Add(CreateClassDistributionRow(list[index], index, maxGames));
			}
		}

		private static UIElement CreateClassDistributionRow(
			MetaDashboardClassDistribution distribution,
			int rowIndex,
			int maxGames)
		{
			var row = new StackPanel
			{
				Margin = new Thickness(0, rowIndex == 0 ? 0 : 7, 0, 0),
				ToolTip = distribution.ToolTip
			};

			var header = new Grid { ToolTip = distribution.ToolTip };
			header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			row.Children.Add(header);

			var className = string.IsNullOrWhiteSpace(distribution.ClassName)
				? distribution.PlayerClass
				: distribution.ClassName;
			var label = new TextBlock
			{
				Text = className,
				Foreground = Brush("#FFD6E2EE"),
				FontSize = 11.5,
				FontWeight = FontWeights.SemiBold,
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center,
				ToolTip = distribution.ToolTip
			};
			Grid.SetColumn(label, 0);
			header.Children.Add(label);

			var classDetail = new TextBlock
			{
				Text = distribution.Games.ToString(CultureInfo.InvariantCulture) + "\u5c40 / " +
					FormatPercent(distribution.SamplePct, 1) + "%",
				Foreground = Brush("#FFA4B8CC"),
				FontSize = 10.5,
				Margin = new Thickness(8, 0, 0, 0),
				VerticalAlignment = VerticalAlignment.Center,
				ToolTip = distribution.ToolTip
			};
			Grid.SetColumn(classDetail, 1);
			header.Children.Add(classDetail);

			var host = new Grid
			{
				Width = DistributionBarWidth,
				Height = DistributionBarHeight,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(0, 2, 0, 0),
				Background = Brush("#301F2A34"),
				ToolTip = distribution.ToolTip,
				ClipToBounds = true
			};
			var activeWidth = maxGames > 0
				? DistributionBarWidth * Clamp(distribution.Games / (double)maxGames, 0.0, 1.0)
				: DistributionBarWidth;
			if (distribution.Games > 0)
			{
				activeWidth = Math.Max(MinScaledBarWidth, activeWidth);
			}
			var active = new Grid
			{
				Width = activeWidth,
				Height = DistributionBarHeight,
				HorizontalAlignment = HorizontalAlignment.Left,
				ToolTip = distribution.ToolTip,
				ClipToBounds = true
			};
			foreach (var segment in distribution.Segments)
			{
				active.ColumnDefinitions.Add(new ColumnDefinition
				{
					Width = new GridLength(Math.Max(0.01, segment.ClassSamplePct), GridUnitType.Star)
				});
			}

			for (var index = 0; index < distribution.Segments.Count; index++)
			{
				var segment = distribution.Segments[index];
				var fill = new Border
				{
					Background = GetSegmentBrush(segment.PlayerClass, index),
					BorderBrush = Brush("#66171E27"),
					BorderThickness = new Thickness(index == 0 ? 0 : 1, 0, 0, 0),
					ToolTip = segment.ToolTip
				};
				if (segment.ClassSamplePct >= 8.0 && activeWidth * segment.ClassSamplePct / 100.0 >= 14.0)
				{
					fill.Child = new TextBlock
					{
						Text = (index + 1).ToString(CultureInfo.InvariantCulture),
						Foreground = Brush("#F2FFFFFF"),
						FontSize = 9.5,
						FontWeight = FontWeights.SemiBold,
						TextAlignment = TextAlignment.Center,
						HorizontalAlignment = HorizontalAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center,
						ToolTip = segment.ToolTip
					};
				}
				Grid.SetColumn(fill, index);
				active.Children.Add(fill);
			}
			host.Children.Add(active);
			row.Children.Add(host);
			row.Children.Add(CreateSegmentLegend(distribution));
			return row;
		}

		private static UIElement CreateSegmentLegend(MetaDashboardClassDistribution distribution)
		{
			var legend = new WrapPanel
			{
				Margin = new Thickness(0, 3, 0, 0),
				ToolTip = distribution.ToolTip
			};
			for (var index = 0; index < distribution.Segments.Count; index++)
			{
				var segment = distribution.Segments[index];
				var item = new StackPanel
				{
					Orientation = Orientation.Horizontal,
					Margin = new Thickness(0, 0, 9, 1),
					ToolTip = segment.ToolTip
				};
				item.Children.Add(new Border
				{
					Width = 11,
					Height = 11,
					Margin = new Thickness(0, 1, 3, 0),
					Background = GetSegmentBrush(segment.PlayerClass, index),
					Child = new TextBlock
					{
						Text = (index + 1).ToString(CultureInfo.InvariantCulture),
						Foreground = Brush("#F2FFFFFF"),
						FontSize = 7.5,
						FontWeight = FontWeights.SemiBold,
						TextAlignment = TextAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center,
						HorizontalAlignment = HorizontalAlignment.Center
					}
				});
				item.Children.Add(new TextBlock
				{
					Text = segment.Title + " " + segment.Games.ToString(CultureInfo.InvariantCulture) +
						"\u5c40/" + FormatPercent(segment.ClassSamplePct, 0) + "%",
					Foreground = Brush("#FFBFD0E0"),
					FontSize = 9.5,
					VerticalAlignment = VerticalAlignment.Center,
					TextTrimming = TextTrimming.CharacterEllipsis,
					ToolTip = segment.ToolTip
				});
				legend.Children.Add(item);
			}
			return legend;
		}

		private static void FillItems(
			Panel target, IEnumerable<MetaDashboardItem> items,
			string emptyText)
		{
			target.Children.Clear();
			var list = items == null
				? new List<MetaDashboardItem>()
				: items.ToList();
			if (list.Count == 0)
			{
				target.Children.Add(new TextBlock
				{
					Text = emptyText,
					Foreground = Brush("#FF8FA1B2"),
					FontSize = 12,
					TextWrapping = TextWrapping.Wrap,
					ToolTip = emptyText
				});
				return;
			}

			for (var index = 0; index < list.Count; index++)
			{
				var item = list[index];
				var itemToolTip = string.IsNullOrWhiteSpace(item.ToolTip)
					? item.Title
					: item.ToolTip;
				var line = new Grid
				{
					Margin = new Thickness(0, index == 0 ? 0 : 3, 0, 0),
					ToolTip = itemToolTip
				};
				line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
				line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

				var rank = new TextBlock
				{
					Text = (index + 1).ToString(CultureInfo.InvariantCulture),
					Foreground = Brush("#FFB7C8D8"),
					FontSize = 12,
					FontWeight = FontWeights.SemiBold,
					VerticalAlignment = VerticalAlignment.Center
				};
				Grid.SetColumn(rank, 0);
				line.Children.Add(rank);

				var itemTitle = new TextBlock
				{
					Text = item.Title,
					Foreground = Brush("#FFF2F6FA"),
					FontSize = 12,
					TextTrimming = TextTrimming.CharacterEllipsis,
					VerticalAlignment = VerticalAlignment.Center,
					ToolTip = itemToolTip
				};
				Grid.SetColumn(itemTitle, 1);
				line.Children.Add(itemTitle);

				var detail = new TextBlock
				{
					Text = item.Detail,
					Foreground = Brush("#FFA4B8CC"),
					FontSize = 11,
					Margin = new Thickness(8, 0, 0, 0),
					VerticalAlignment = VerticalAlignment.Center,
					ToolTip = itemToolTip
				};
				Grid.SetColumn(detail, 2);
				line.Children.Add(detail);

				target.Children.Add(line);
			}
		}

		private static Brush GetSegmentBrush(string playerClass, int segmentIndex)
		{
			var color = GetClassColor(playerClass);
			var darken = Math.Min(0.42, segmentIndex * 0.12);
			var lighten = segmentIndex % 2 == 0
				? Math.Min(0.16, segmentIndex * 0.03)
				: Math.Min(0.28, 0.12 + segmentIndex * 0.03);
			return new SolidColorBrush(Color.FromRgb(
				Blend(color.R, darken, lighten),
				Blend(color.G, darken, lighten),
				Blend(color.B, darken, lighten)));
		}

		private static Color GetClassColor(string playerClass)
		{
			switch ((playerClass ?? "")
				.Replace(" ", "")
				.Replace("_", "")
				.Replace("-", "")
				.Trim()
				.ToUpperInvariant())
			{
				case "DEATHKNIGHT":
					return Color.FromRgb(196, 55, 76);
				case "DEMONHUNTER":
					return Color.FromRgb(120, 204, 72);
				case "DRUID":
					return Color.FromRgb(214, 133, 56);
				case "HUNTER":
					return Color.FromRgb(92, 177, 79);
				case "MAGE":
					return Color.FromRgb(75, 165, 226);
				case "PALADIN":
					return Color.FromRgb(223, 180, 86);
				case "PRIEST":
					return Color.FromRgb(214, 220, 226);
				case "ROGUE":
					return Color.FromRgb(219, 199, 79);
				case "SHAMAN":
					return Color.FromRgb(71, 126, 214);
				case "WARLOCK":
					return Color.FromRgb(153, 91, 203);
				case "WARRIOR":
					return Color.FromRgb(202, 88, 63);
				default:
					return Color.FromRgb(132, 150, 166);
			}
		}

		private static byte Blend(byte value, double darken, double lighten)
		{
			var darkened = value * (1.0 - darken);
			var mixed = darkened + (255.0 - darkened) * lighten;
			return (byte)Math.Max(0, Math.Min(255, mixed));
		}

		private static string FormatPercent(double value, int digits)
		{
			return Math.Round(value, digits, MidpointRounding.AwayFromZero)
				.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
		}

		private static double Clamp(double value, double min, double max)
		{
			if (value < min)
			{
				return min;
			}
			if (value > max)
			{
				return max;
			}
			return value;
		}

		private static Brush Brush(string color)
		{
			return (Brush)new BrushConverter().ConvertFromString(color);
		}
	}
}
