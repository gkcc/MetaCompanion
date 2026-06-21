using System;
using System.Collections.Generic;
using System.Diagnostics;
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
		private const double ClassOverviewBarHeight = 10.0;
		private const double DistributionBarHeight = 16.0;
		private const double MinScaledBarWidth = 18.0;
		private const string DashboardToolTip =
			"\u4f20\u7edf\u5bf9\u6218\u5165\u53e3\u663e\u793a\u3002\u63a8\u8350\u6765\u81ea HSReplay \u5bf9\u9635\u80dc\u7387\u548c\u672c\u5730\u8fd1\u671f\u5bf9\u624b\u5206\u5e03\uff0c\u4e0d\u662f\u5f53\u524d\u5bf9\u624b\u5b9e\u65f6\u8bc6\u522b\u3002\u8fdb\u5165\u5b9e\u9645\u5bf9\u5c40\u540e\u4f1a\u81ea\u52a8\u9690\u85cf\u3002";
		private const string RecommendationsToolTip =
			"\u6d41\u6d3e\u63a8\u8350\u6309 HSReplay \u5bf9\u9635\u77e9\u9635\u80dc\u7387\u548c\u672c\u5730\u5f53\u524d\u8865\u4e01/\u8fd1\u671f\u5bf9\u624b\u5206\u5e03\u52a0\u6743\u6392\u5e8f\uff1b\u672c\u5730\u6837\u672c\u6309\u65f6\u95f4\u8870\u51cf\uff0c\u9ed8\u8ba4\u672c\u5730\u5206\u5e03\u6743\u91cd 35%\u3002";
		private const string EnvironmentToolTip =
			"\u8fd1\u671f\u5bf9\u624b\u6765\u81ea HDT \u672c\u5730\u5386\u53f2\uff1b\u6837\u672c\u7a97\u53e3\u4f18\u5148\u4f7f\u7528\u5f53\u524d\u8865\u4e01\uff0c\u804c\u4e1a\u548c\u5f62\u6001\u9891\u6b21\u6309\u539f\u59cb\u5c40\u6570\u5c55\u793a\u3002";
		private const string EnvironmentClassToolTip =
			"\u6309\u5bf9\u624b\u804c\u4e1a\u5408\u8ba1\u7684\u5c40\u6570\u548c\u5360\u6bd4\uff1b\u8272\u5757\u662f\u8be5\u804c\u4e1a\u4e0b\u7684\u6d41\u6d3e\u62c6\u5206\u3002\u4f8b\u5982\u7267\u5e08 6 \u5c40\u53ef\u80fd\u7531\u4efb\u52a1\u7267 5 \u5c40 + \u63a7\u5236\u7267 1 \u5c40\u7ec4\u6210\u3002";
		private const string EnvironmentArchetypeToolTip =
			"\u6309\u5355\u4e2a\u6d41\u6d3e\u7edf\u8ba1\u7684\u6392\u884c\uff0c\u8fd9\u91cc\u7684\u5c40\u6570\u4e0d\u662f\u804c\u4e1a\u5408\u8ba1\uff1b\u5360\u6bd4\u5206\u6bcd\u662f\u8fd1\u671f\u5168\u90e8\u5df2\u8bc6\u522b\u5bf9\u5c40\u3002";
		private const string LastGameToolTip =
			"\u6700\u8fd1\u4e00\u5c40\u7684\u8bc6\u522b\u8be6\u60c5\uff1b\u53ef\u4ee5\u5728\u8fd9\u91cc\u628a\u5f62\u6001\u4fee\u6b63\u5230\u672c\u5730 TSV\uff0c\u4e0d\u4f1a\u8986\u76d6\u539f\u59cb\u5bf9\u5c40\u5386\u53f2\u3002";

		private readonly Action _closeAction;
		private readonly Func<string, string, bool> _correctionAction;
		private readonly TextBlock _title;
		private readonly TextBlock _subtitle;
		private readonly StackPanel _lastGame;
		private readonly StackPanel _recommendations;
		private readonly StackPanel _environmentChart;
		private readonly StackPanel _environment;
		private readonly Grid _header;
		private TextBox _correctionTextBox;
		private MetaDashboardLastGame _currentLastGame;

		public MetaDashboardPanel(Action closeAction)
			: this(closeAction, null)
		{
		}

		public MetaDashboardPanel(Action closeAction, Func<string, string, bool> correctionAction)
		{
			_closeAction = closeAction;
			_correctionAction = correctionAction;
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

			root.Children.Add(SectionTitle("\u6700\u8fd1\u4e00\u5c40\u8bc6\u522b", LastGameToolTip));
			_lastGame = new StackPanel { Margin = new Thickness(0, 4, 0, 9) };
			root.Children.Add(_lastGame);

			root.Children.Add(SectionTitle("\u63a8\u8350\u6d41\u6d3e", RecommendationsToolTip));
			_recommendations = new StackPanel { Margin = new Thickness(0, 4, 0, 9) };
			root.Children.Add(_recommendations);

			root.Children.Add(SectionTitle("\u8fd1\u671f\u5bf9\u624b\uff1a\u804c\u4e1a\u5408\u8ba1", EnvironmentClassToolTip));
			_environmentChart = new StackPanel { Margin = new Thickness(0, 5, 0, 6) };
			root.Children.Add(_environmentChart);
			root.Children.Add(SectionTitle("\u8fd1\u671f\u6d41\u6d3e\uff1a\u5355\u9879\u6392\u884c", EnvironmentArchetypeToolTip));
			_environment = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
			root.Children.Add(_environment);
		}

		public UIElement DragHandle => _header;
		internal StackPanel LastGamePanel => _lastGame;
		internal StackPanel EnvironmentChartPanel => _environmentChart;
		internal StackPanel EnvironmentListPanel => _environment;

		public void Update(string title, MetaDashboardSnapshot snapshot)
		{
			_title.Text = title ?? "\u5361\u7ec4\u6d41\u6d3e\u63a8\u8350";
			_title.ToolTip = DashboardToolTip;
			if (snapshot == null || (!snapshot.HasContent && snapshot.LastGame == null))
			{
				_subtitle.Text = "\u6682\u65e0\u672c\u5730\u63a8\u8350\u7f13\u5b58";
				FillItems(_recommendations, Enumerable.Empty<MetaDashboardItem>(),
					"\u5148\u8fd0\u884c\u4e00\u6b21\u6570\u636e\u66f4\u65b0\u540e\u663e\u793a\u63a8\u8350");
				FillLastGame(null);
				FillEnvironmentChart(_environmentChart, Enumerable.Empty<MetaDashboardClassDistribution>());
				FillItems(_environment, Enumerable.Empty<MetaDashboardItem>(), "\u6682\u65e0\u8fd1\u671f\u5bf9\u624b\u5206\u5e03");
				return;
			}

			_subtitle.Text = BuildSubtitle(snapshot);
			_subtitle.ToolTip = BuildSubtitleToolTip(snapshot);
			FillLastGame(snapshot.LastGame);
			FillItems(_recommendations, snapshot.Recommendations, "\u6682\u65e0\u63a8\u8350\u7ed3\u679c");
			FillEnvironmentChart(_environmentChart, snapshot.EnvironmentClasses);
			FillItems(_environment, snapshot.Environment, "\u6682\u65e0\u8fd1\u671f\u5bf9\u624b\u5206\u5e03");
		}

		private void FillLastGame(MetaDashboardLastGame item)
		{
			_currentLastGame = item;
			_correctionTextBox = null;
			_lastGame.Children.Clear();
			if (item == null)
			{
				_lastGame.Children.Add(new TextBlock
				{
					Text = "\u6682\u65e0\u6700\u8fd1\u4e00\u5c40\u8bc6\u522b\u8bb0\u5f55",
					Foreground = Brush("#FF8FA1B2"),
					FontSize = 12,
					TextWrapping = TextWrapping.Wrap,
					ToolTip = LastGameToolTip
				});
				return;
			}

			_lastGame.Children.Add(new TextBlock
			{
				Text = item.Title,
				Foreground = Brush("#FFF2F6FA"),
				FontSize = 12,
				FontWeight = FontWeights.SemiBold,
				TextTrimming = TextTrimming.CharacterEllipsis,
				ToolTip = item.ToolTip
			});
			if (!string.IsNullOrWhiteSpace(item.Detail))
			{
				_lastGame.Children.Add(new TextBlock
				{
					Text = item.Detail,
					Foreground = Brush("#FFA4B8CC"),
					FontSize = 11,
					Margin = new Thickness(0, 1, 0, 0),
					TextWrapping = TextWrapping.Wrap,
					ToolTip = item.ToolTip
				});
			}
			if (item.IsLowConfidence)
			{
				_lastGame.Children.Add(new TextBlock
				{
					Text = "\u4f4e\u7f6e\u4fe1\uff0c\u4ec5\u4f9b\u53c2\u8003",
					Foreground = Brush("#FFFFC857"),
					FontSize = 11,
					FontWeight = FontWeights.SemiBold,
					Margin = new Thickness(0, 2, 0, 0),
					ToolTip = item.ToolTip
				});
			}

			AddCandidateRows(item);
			AddEvidenceLine(item);
			AddLastGameActions(item);
		}

		private void AddCandidateRows(MetaDashboardLastGame item)
		{
			if (item.Candidates == null || item.Candidates.Count == 0)
			{
				return;
			}

			for (var index = 0; index < item.Candidates.Take(3).Count(); index++)
			{
				var candidate = item.Candidates[index];
				_lastGame.Children.Add(new TextBlock
				{
					Text = (index + 1).ToString(CultureInfo.InvariantCulture) + ". " +
						candidate.Name + " " +
						candidate.ConfidencePercent.ToString(CultureInfo.InvariantCulture) +
						"% score " + candidate.Score.ToString(CultureInfo.InvariantCulture) +
						" branchCount " + candidate.BranchCount.ToString(CultureInfo.InvariantCulture),
					Foreground = Brush("#FFBFD0E0"),
					FontSize = 10.5,
					Margin = new Thickness(0, index == 0 ? 4 : 1, 0, 0),
					TextTrimming = TextTrimming.CharacterEllipsis,
					ToolTip = item.ToolTip
				});
			}
		}

		private void AddEvidenceLine(MetaDashboardLastGame item)
		{
			if (item.KeyEvidenceCards == null || item.KeyEvidenceCards.Count == 0)
			{
				return;
			}

			_lastGame.Children.Add(new TextBlock
			{
				Text = "\u5173\u952e\u8bc1\u636e: " + string.Join(", ", item.KeyEvidenceCards.ToArray()),
				Foreground = Brush("#FFA4B8CC"),
				FontSize = 10.5,
				Margin = new Thickness(0, 4, 0, 0),
				TextWrapping = TextWrapping.Wrap,
				ToolTip = item.ToolTip
			});
		}

		private void AddLastGameActions(MetaDashboardLastGame item)
		{
			var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
			if (!string.IsNullOrWhiteSpace(item.HsReplayUrl))
			{
				links.Children.Add(ActionButton("HSReplay", () => OpenExternal(item.HsReplayUrl)));
			}
			if (!string.IsNullOrWhiteSpace(item.ReplayPath))
			{
				links.Children.Add(ActionButton("\u672c\u5730\u5f55\u50cf", () => OpenExternal(item.ReplayPath)));
			}
			if (links.Children.Count > 0)
			{
				_lastGame.Children.Add(links);
			}

			var names = item.Candidates == null
				? new List<string>()
				: item.Candidates
					.Select(candidate => candidate.Name)
					.Where(name => !string.IsNullOrWhiteSpace(name))
					.Distinct()
					.ToList();

			if (names.Count > 0)
			{
				var candidates = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
				foreach (var name in names.Take(3))
				{
					var candidateName = name;
					var candidateButton = ActionButton(candidateName, () =>
					{
						if (_correctionTextBox == null)
						{
							return;
						}

						_correctionTextBox.Text = candidateName;
						_correctionTextBox.CaretIndex = _correctionTextBox.Text.Length;
						_correctionTextBox.Focus();
					});
					candidateButton.MinWidth = 0;
					candidateButton.ToolTip = "\u586b\u5165\u4fee\u6b63\u5f62\u6001: " + candidateName;
					candidates.Children.Add(candidateButton);
				}
				_lastGame.Children.Add(candidates);
			}

			var correction = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
			_correctionTextBox = new TextBox
			{
				Width = 210,
				MinHeight = 24,
				Text = names.Count > 0 ? names[0] : item.Title,
				ToolTip = "\u8f93\u5165\u6b63\u786e\u5f62\u6001\u540d\uff1b\u4e5f\u53ef\u4ee5\u70b9\u4e0a\u65b9\u5019\u9009\u5feb\u901f\u586b\u5165"
			};
			correction.Children.Add(_correctionTextBox);

			var button = ActionButton("\u4fee\u6b63\u672c\u5c40", ApplyCorrection);
			button.Margin = new Thickness(6, 0, 0, 0);
			button.IsEnabled = _correctionAction != null &&
				!string.IsNullOrWhiteSpace(item.MatchId);
			correction.Children.Add(button);
			_lastGame.Children.Add(correction);
		}

		private Button ActionButton(string text, Action action)
		{
			var button = new Button
			{
				Content = text,
				MinWidth = 58,
				MinHeight = 24,
				Padding = new Thickness(8, 2, 8, 2),
				Margin = new Thickness(0, 0, 6, 0),
				FontSize = 10.5
			};
			button.Click += (sender, args) => action?.Invoke();
			return button;
		}

		private void ApplyCorrection()
		{
			if (_currentLastGame == null || _correctionAction == null)
			{
				return;
			}

			var correctedArchetype = _correctionTextBox == null ? "" : _correctionTextBox.Text;
			if (string.IsNullOrWhiteSpace(correctedArchetype))
			{
				MessageBox.Show(
					"\u8bf7\u5148\u8f93\u5165\u8981\u4fee\u6b63\u7684\u5f62\u6001\u540d\u3002",
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
				return;
			}

			if (_correctionAction(_currentLastGame.MatchId, correctedArchetype.Trim()))
			{
				MessageBox.Show(
					"\u5df2\u8bb0\u5f55\u4fee\u6b63\uff0c\u5e76\u5c1d\u8bd5\u5237\u65b0\u672c\u5730\u73af\u5883\u7edf\u8ba1\u3002",
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
			}
		}

		private static void OpenExternal(string target)
		{
			try
			{
				Process.Start(target);
			}
			catch (Exception ex)
			{
				MessageBox.Show(
					"\u6253\u5f00\u5931\u8d25: " + ex.Message,
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}
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
			target.Children.Add(CreateClassOverviewStrip(list));
			for (var index = 0; index < list.Count; index++)
			{
				target.Children.Add(CreateClassDistributionRow(list[index], index, maxGames));
			}
		}

		private static string BuildSubtitle(MetaDashboardSnapshot snapshot)
		{
			var text = snapshot.UpdatedAt.HasValue
				? "\u66f4\u65b0 " + snapshot.UpdatedAt.Value.ToString("MM-dd HH:mm")
				: "\u8bfb\u53d6\u672c\u5730\u7f13\u5b58";
			var sampleGames = GetSampleGames(snapshot);
			var remoteSource = GetRemoteSourceText(snapshot);
			var sampleText = sampleGames > 0
				? text + " \u00b7 \u6837\u672c " + sampleGames.ToString(CultureInfo.InvariantCulture) + "\u5c40"
				: text;
			return string.IsNullOrWhiteSpace(remoteSource)
				? sampleText
				: sampleText + " \u00b7 \u8fdc\u7a0b " + remoteSource;
		}

		private static string BuildSubtitleToolTip(MetaDashboardSnapshot snapshot)
		{
			var lines = new List<string> { DashboardToolTip };
			var sampleGames = GetSampleGames(snapshot);
			if (sampleGames > 0)
			{
				lines.Add("\u672c\u5730\u6837\u672c " +
					sampleGames.ToString(CultureInfo.InvariantCulture) +
					" \u5c40\uff1b\u4e0a\u65b9\u804c\u4e1a\u662f\u5408\u8ba1\uff0c\u4e0b\u65b9\u6d41\u6d3e\u662f\u5355\u9879\u6392\u884c\u3002");
			}

			if (snapshot != null && snapshot.RemoteSource != null && snapshot.RemoteSource.HasData)
			{
				lines.Add(snapshot.RemoteSource.ToolTip);
			}
			return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
		}

		private static int GetSampleGames(MetaDashboardSnapshot snapshot)
		{
			return snapshot != null && snapshot.EnvironmentClasses != null
				? snapshot.EnvironmentClasses.Sum(item => item.Games)
				: 0;
		}

		private static string GetRemoteSourceText(MetaDashboardSnapshot snapshot)
		{
			return snapshot != null &&
				snapshot.RemoteSource != null &&
				snapshot.RemoteSource.HasData
				? snapshot.RemoteSource.ShortText
				: "";
		}

		private static UIElement CreateClassOverviewStrip(List<MetaDashboardClassDistribution> classes)
		{
			var totalGames = classes.Sum(item => item.Games);
			var toolTip = BuildClassOverviewToolTip(classes, totalGames);
			var host = new Grid
			{
				Width = DistributionBarWidth,
				Height = ClassOverviewBarHeight,
				HorizontalAlignment = HorizontalAlignment.Left,
				Margin = new Thickness(0, 0, 0, 7),
				Background = Brush("#301F2A34"),
				ToolTip = toolTip,
				ClipToBounds = true
			};

			for (var index = 0; index < classes.Count; index++)
			{
				var distribution = classes[index];
				host.ColumnDefinitions.Add(new ColumnDefinition
				{
					Width = new GridLength(Math.Max(0.01, distribution.Games), GridUnitType.Star)
				});

				var fill = new Border
				{
					Background = new SolidColorBrush(GetClassColor(distribution.PlayerClass)),
					BorderBrush = Brush("#66171E27"),
					BorderThickness = new Thickness(index == 0 ? 0 : 1, 0, 0, 0),
					ToolTip = distribution.ToolTip
				};
				Grid.SetColumn(fill, index);
				host.Children.Add(fill);
			}

			return host;
		}

		private static string BuildClassOverviewToolTip(
			List<MetaDashboardClassDistribution> classes,
			int totalGames)
		{
			var lines = new List<string>
			{
				"\u5168\u6837\u672c\u804c\u4e1a\u5360\u6bd4\uff08" +
					totalGames.ToString(CultureInfo.InvariantCulture) + " \u5c40\uff09"
			};
			lines.AddRange(classes.Select(distribution =>
			{
				var className = string.IsNullOrWhiteSpace(distribution.ClassName)
					? distribution.PlayerClass
					: distribution.ClassName;
				return className + " " + distribution.Games.ToString(CultureInfo.InvariantCulture) +
					" \u5c40 / " + FormatPercent(distribution.SamplePct, 1) + "%";
			}));
			return string.Join("\n", lines);
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
				var segmentBrush = GetSegmentBrush(segment.PlayerClass, index);
				var fill = new Border
				{
					Background = segmentBrush,
					BorderBrush = Brush("#66171E27"),
					BorderThickness = new Thickness(index == 0 ? 0 : 1, 0, 0, 0),
					ToolTip = segment.ToolTip
				};
				if (segment.ClassSamplePct >= 8.0 && activeWidth * segment.ClassSamplePct / 100.0 >= 14.0)
				{
					fill.Child = new TextBlock
					{
						Text = (index + 1).ToString(CultureInfo.InvariantCulture),
						Foreground = GetSegmentTextBrush(segment.PlayerClass, index),
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
				var segmentBrush = GetSegmentBrush(segment.PlayerClass, index);
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
					Background = segmentBrush,
					Child = new TextBlock
					{
						Text = (index + 1).ToString(CultureInfo.InvariantCulture),
						Foreground = GetSegmentTextBrush(segment.PlayerClass, index),
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
			return new SolidColorBrush(GetSegmentColor(playerClass, segmentIndex));
		}

		private static Brush GetSegmentTextBrush(string playerClass, int segmentIndex)
		{
			return new SolidColorBrush(GetReadableTextColor(GetSegmentColor(playerClass, segmentIndex)));
		}

		internal static Color GetSegmentColor(string playerClass, int segmentIndex)
		{
			var color = GetClassColor(playerClass);
			var darken = Math.Min(0.42, segmentIndex * 0.12);
			var lighten = segmentIndex % 2 == 0
				? Math.Min(0.16, segmentIndex * 0.03)
				: Math.Min(0.28, 0.12 + segmentIndex * 0.03);
			return Color.FromRgb(
				Blend(color.R, darken, lighten),
				Blend(color.G, darken, lighten),
				Blend(color.B, darken, lighten));
		}

		internal static Color GetClassColor(string playerClass)
		{
			switch ((playerClass ?? "")
				.Replace(" ", "")
				.Replace("_", "")
				.Replace("-", "")
				.Trim()
				.ToUpperInvariant())
			{
				case "DEATHKNIGHT":
					return Color.FromRgb(196, 30, 58);
				case "DEMONHUNTER":
					return Color.FromRgb(163, 48, 201);
				case "DRUID":
					return Color.FromRgb(255, 124, 10);
				case "EVOKER":
					return Color.FromRgb(51, 147, 127);
				case "HUNTER":
					return Color.FromRgb(170, 211, 114);
				case "MAGE":
					return Color.FromRgb(63, 199, 235);
				case "MONK":
					return Color.FromRgb(0, 255, 152);
				case "PALADIN":
					return Color.FromRgb(244, 140, 186);
				case "PRIEST":
					return Color.FromRgb(255, 255, 255);
				case "ROGUE":
					return Color.FromRgb(255, 244, 104);
				case "SHAMAN":
					return Color.FromRgb(0, 112, 221);
				case "WARLOCK":
					return Color.FromRgb(135, 136, 238);
				case "WARRIOR":
					return Color.FromRgb(198, 155, 109);
				default:
					return Color.FromRgb(132, 150, 166);
			}
		}

		private static Color GetReadableTextColor(Color background)
		{
			var luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255.0;
			return luminance > 0.62
				? Color.FromRgb(23, 30, 39)
				: Color.FromRgb(255, 255, 255);
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
