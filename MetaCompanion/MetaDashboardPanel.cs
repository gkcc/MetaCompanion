using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MetaCompanion
{
	internal class MetaDashboardPanel : Border
	{
		private const string DashboardToolTip =
			"环境速览显示本地缓存：个人推荐、近期对手分布和最近一局。它不是当前对手实时识别；实际对战中会自动隐藏，赛后或每日刷新后会更新。";
		private const string RecommendationsToolTip =
			"推荐形态按 HSReplay 对阵矩阵胜率与本地近 3 天对手分布加权排序，默认本地权重 35%。";
		private const string EnvironmentToolTip =
			"近期对手来自 HDT 本地历史，默认统计近 3 天，并按识别置信度计权。";
		private const string LastGameToolTip =
			"最近一局来自本地对局历史，可打开 HSReplay 或本地录像复盘。";

		private readonly Action _closeAction;
		private readonly TextBlock _title;
		private readonly TextBlock _subtitle;
		private readonly StackPanel _recommendations;
		private readonly StackPanel _environment;
		private readonly TextBlock _lastGame;
		private readonly StackPanel _lastGameButtons;
		private readonly Grid _header;

		public MetaDashboardPanel(Action closeAction)
		{
			_closeAction = closeAction;
			Width = 338;
			Background = Brush("#EA171E27");
			BorderBrush = Brush("#806D7C8C");
			BorderThickness = new Thickness(1);
			CornerRadius = new CornerRadius(4);
			Padding = new Thickness(12);
			IsHitTestVisible = true;

			var root = new StackPanel();
			Child = root;

			_header = new Grid { Cursor = Cursors.SizeAll };
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
				Content = "×",
				Width = 24,
				Height = 22,
				Padding = new Thickness(0),
				Margin = new Thickness(8, 0, 0, 0),
				Cursor = Cursors.Arrow,
				ToolTip = "关闭"
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

			root.Children.Add(SectionTitle("推荐形态", RecommendationsToolTip));
			_recommendations = new StackPanel { Margin = new Thickness(0, 4, 0, 9) };
			root.Children.Add(_recommendations);

			root.Children.Add(SectionTitle("近期对手", EnvironmentToolTip));
			_environment = new StackPanel { Margin = new Thickness(0, 4, 0, 9) };
			root.Children.Add(_environment);

			root.Children.Add(SectionTitle("最近一局", LastGameToolTip));
			_lastGame = new TextBlock
			{
				Foreground = Brush("#FFE7EDF2"),
				FontSize = 12,
				TextWrapping = TextWrapping.Wrap,
				ToolTip = LastGameToolTip
			};
			root.Children.Add(_lastGame);

			_lastGameButtons = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				Margin = new Thickness(0, 7, 0, 0)
			};
			root.Children.Add(_lastGameButtons);
		}

		public UIElement DragHandle => _header;

		public void Update(string title, MetaDashboardSnapshot snapshot)
		{
			_title.Text = title ?? "标准环境";
			_title.ToolTip = DashboardToolTip;
			if (snapshot == null || !snapshot.HasContent)
			{
				_subtitle.Text = "暂无本地推荐缓存";
				FillItems(_recommendations, Enumerable.Empty<MetaDashboardItem>(),
					"运行 LocalMeta + PersonalRecommendations 后显示");
				FillItems(_environment, Enumerable.Empty<MetaDashboardItem>(), "暂无近期对手分布");
				UpdateLastGame(null);
				return;
			}

			_subtitle.Text = snapshot.UpdatedAt.HasValue
				? "更新 " + snapshot.UpdatedAt.Value.ToString("MM-dd HH:mm")
				: "读取本地缓存";
			_subtitle.ToolTip = DashboardToolTip;
			FillItems(_recommendations, snapshot.Recommendations, "暂无推荐结果");
			FillItems(_environment, snapshot.Environment, "暂无近期对手分布");
			UpdateLastGame(snapshot.LastGame);
		}

		private void UpdateLastGame(MetaDashboardItem lastGame)
		{
			_lastGameButtons.Children.Clear();
			if (lastGame == null)
			{
				_lastGame.Text = "暂无赛后识别结果";
				_lastGame.ToolTip = LastGameToolTip;
				return;
			}

			_lastGame.Text = lastGame.Title + "  " + lastGame.Detail;
			_lastGame.ToolTip = string.IsNullOrWhiteSpace(lastGame.ToolTip)
				? LastGameToolTip
				: lastGame.ToolTip;
			if (!string.IsNullOrWhiteSpace(lastGame.HsReplayUrl))
			{
				_lastGameButtons.Children.Add(LinkButton(
					"打开 HSReplay", () => Process.Start(lastGame.HsReplayUrl)));
			}
			if (!string.IsNullOrWhiteSpace(lastGame.ReplayPath) && File.Exists(lastGame.ReplayPath))
			{
				_lastGameButtons.Children.Add(LinkButton(
					"打开本地录像", () => Process.Start(lastGame.ReplayPath)));
			}
		}

		private static Button LinkButton(string text, Action click)
		{
			var button = new Button
			{
				Content = text,
				FontSize = 11,
				Padding = new Thickness(8, 3, 8, 3),
				Margin = new Thickness(0, 0, 6, 0)
			};
			button.Click += (sender, args) => click();
			return button;
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

		private static void FillItems(
			Panel target, System.Collections.Generic.IEnumerable<MetaDashboardItem> items,
			string emptyText)
		{
			target.Children.Clear();
			var list = items == null
				? new System.Collections.Generic.List<MetaDashboardItem>()
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
					Text = (index + 1).ToString(),
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

		private static Brush Brush(string color)
		{
			return (Brush)new BrushConverter().ConvertFromString(color);
		}
	}
}

