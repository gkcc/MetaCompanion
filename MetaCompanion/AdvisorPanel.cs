using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MetaCompanion
{
	/// <summary>
	/// Code-built overlay content for the live advisor. Keeping this view out of XAML makes
	/// it safe to load from the HDT plugin assembly without adding another WPF Page resource.
	/// </summary>
	internal sealed class AdvisorPanel : Border
	{
		private const int MaximumRecommendations = 3;
		internal const double CompactPanelWidth = 380;
		private const double CardArtWidth = 34;
		private const double CardArtHeight = 28;
		internal const string WarningHeading = "模型覆盖提示（不是故障）：";
		internal const string BehaviorReferenceHeading = "你过去的打法参考（不代表最优）";
		internal const string BehaviorReferenceDisclosure =
			"仅对 HDT 已确认的完整合法动作排序；来自你的历史选择，不是胜率或强化学习最优结论，也不会自动出牌。";
		private const string GeneralToolTip =
			"已证明斩杀仅表示受支持的公开通用规则模型内成立；只有标为“回应已验证”的路线才完成了当前可见范围内的最坏回应校验。其余数值是未校准的战术评分，不是真实胜率；建议不会自动执行。" +
			"历史打法参考只排序 HDT 已确认的合法动作，不代表最优或胜率。" +
			AdvisorUserMessages.DeveloperLogHint;

		private readonly Action _closeAction;
		private readonly Grid _header;
		private readonly TextBlock _subtitle;
		private readonly Border _statusBorder;
		private readonly TextBlock _statusText;
		private readonly StackPanel _recommendations;
		private readonly StackPanel _behaviorReferences;
		private readonly StackPanel _warnings;
		private static readonly object CardArtCacheLock = new object();
		private static readonly Dictionary<string, ImageSource> CardArtCache =
			new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
		private AdvisorGameState _gameState;

		public AdvisorPanel(Action closeAction)
		{
			_closeAction = closeAction;
			Width = CompactPanelWidth;
			MinHeight = 120;
			Background = Brush("#ED151C25");
			BorderBrush = Brush("#8A6D7C8C");
			BorderThickness = new Thickness(1);
			CornerRadius = new CornerRadius(5);
			Padding = new Thickness(9);
			IsHitTestVisible = true;
			ToolTip = GeneralToolTip;

			var root = new Grid();
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			Child = root;

			_header = new Grid
			{
				Cursor = Cursors.SizeAll,
				ToolTip = "拖动这里调整建议面板位置"
			};
			_header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			_header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			Grid.SetRow(_header, 0);
			root.Children.Add(_header);

			var title = new TextBlock
			{
				Text = "实战策略建议",
				Foreground = Brush("#FFF4F7FA"),
				FontSize = 14,
				FontWeight = FontWeights.SemiBold,
				VerticalAlignment = VerticalAlignment.Center,
				ToolTip = GeneralToolTip
			};
			Grid.SetColumn(title, 0);
			_header.Children.Add(title);

			var close = new Button
			{
				Content = "×",
				Width = 24,
				Height = 22,
				Padding = new Thickness(0),
				Margin = new Thickness(8, 0, 0, 0),
				Cursor = Cursors.Arrow,
				ToolTip = "本局隐藏建议面板"
			};
			close.Click += (sender, args) => _closeAction?.Invoke();
			Grid.SetColumn(close, 1);
			_header.Children.Add(close);

			_subtitle = new TextBlock
			{
				Foreground = Brush("#FFA4B8CC"),
				FontSize = 10,
				Margin = new Thickness(0, 1, 0, 5),
				TextTrimming = TextTrimming.CharacterEllipsis,
				ToolTip = GeneralToolTip
			};
			Grid.SetRow(_subtitle, 1);
			root.Children.Add(_subtitle);

			_statusText = new TextBlock
			{
				Foreground = Brush("#FFE4EDF5"),
				FontSize = 10.5,
				FontWeight = FontWeights.SemiBold,
				TextWrapping = TextWrapping.Wrap
			};
			_statusBorder = new Border
			{
				CornerRadius = new CornerRadius(3),
				Padding = new Thickness(6, 3, 6, 3),
				Margin = new Thickness(0, 0, 0, 5),
				Child = _statusText
			};
			Grid.SetRow(_statusBorder, 2);
			root.Children.Add(_statusBorder);

			var scroll = new ScrollViewer
			{
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				CanContentScroll = true
			};
			var scrollContent = new StackPanel();
			scroll.Content = scrollContent;
			Grid.SetRow(scroll, 3);
			root.Children.Add(scroll);

			_recommendations = new StackPanel();
			scrollContent.Children.Add(_recommendations);
			_behaviorReferences = new StackPanel();
			scrollContent.Children.Add(_behaviorReferences);
			_warnings = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
			scrollContent.Children.Add(_warnings);

			ShowThinking(null, "等待当前局面");
		}

		public UIElement DragHandle => _header;
		internal StackPanel RecommendationsPanel => _recommendations;

		public string StateId { get; private set; }

		public void ShowThinking(string stateId, string message)
		{
			StateId = stateId ?? "";
			_gameState = null;
			UpdateSubtitle();
			SetStatus(
				AdvisorUserMessages.Status(message, AdvisorUserMessages.Searching),
				"#553A78B5",
				"#995FA8E8");
			FillStateMessage("求解器正在评估合法动作、隐藏信息与后续局面。", "#FF9EB4C8");
		}

		public void ShowWorkerUnavailable(string stateId, string message)
		{
			StateId = stateId ?? "";
			_gameState = null;
			UpdateSubtitle();
			SetStatus("求解服务暂不可用", "#554F2630", "#99D85C70");
			FillStateMessage(
				AdvisorUserMessages.Status(message, AdvisorUserMessages.WorkerUnavailable),
				"#FFFFA6B5");
		}

		public void ShowStale(string stateId, string message)
		{
			StateId = stateId ?? "";
			_gameState = null;
			UpdateSubtitle();
			SetStatus("局面已变化 · 旧建议已清除", "#554D3B1D", "#99D9A441");
			FillStateMessage(
				AdvisorUserMessages.Status(message, AdvisorUserMessages.StaleResult),
				"#FFFFD58A");
		}

		public void Update(AdvisorSolveResponse response, bool isStale)
		{
			Update(response, isStale, null);
		}

		public void Update(
			AdvisorSolveResponse response,
			bool isStale,
			AdvisorGameState gameState)
		{
			if (response == null)
			{
				_gameState = null;
				ShowWorkerUnavailable(StateId, "求解器返回了空结果。HDT 可继续正常使用。");
				return;
			}

			StateId = response.StateId ?? "";
			_gameState = gameState != null && string.Equals(
				gameState.StateId,
				response.StateId,
				StringComparison.Ordinal)
				? gameState
				: null;
			var status = (response.Status ?? "").Trim().ToLowerInvariant();
			if (isStale || status == "stale" || status == "cancelled")
			{
				ShowStale(response.StateId, AdvisorUserMessages.ResponseStatus(
					status, response.Message, response.IsFinal, false));
				return;
			}

			if (status == "unavailable" || status == "error")
			{
				ShowWorkerUnavailable(response.StateId, AdvisorUserMessages.ResponseStatus(
					status, response.Message, response.IsFinal, false));
				FillWarnings(response.Warnings);
				return;
			}

			if (status == "unsupported")
			{
				UpdateSubtitle(response.Coverage);
				SetStatus("模型暂未覆盖当前局面", "#554D3B1D", "#99D9A441");
				FillStateMessage(
					AdvisorUserMessages.ResponseStatus(
						status, response.Message, response.IsFinal, false),
					"#FFFFD58A");
				FillWarnings(BuildOrderingModelNotices(response.Coverage)
					.Concat(response.Warnings ?? new List<string>()));
				return;
			}

			var recommendations = (response.Recommendations ?? new List<AdvisorRecommendation>())
				.Where(item => item != null)
				.OrderBy(item => item.Rank <= 0 ? Int32.MaxValue : item.Rank)
				.ThenByDescending(item => item.ExpectedWinRate)
				.Take(MaximumRecommendations)
				.ToList();
			var showBehaviorReferences = ShouldDisplayBehaviorReferences(
				status, response.BehaviorReferences);

			UpdateSubtitle(response.Coverage);
			if (recommendations.Count == 0 && !showBehaviorReferences)
			{
				if (status == "thinking" || status == "starting" || !response.IsFinal)
				{
					SetStatus("正在搜索可行路线…", "#553A78B5", "#995FA8E8");
					FillStateMessage(
						AdvisorUserMessages.ResponseStatus(
							status, response.Message, response.IsFinal, false),
						"#FF9EB4C8");
				}
				else
				{
					SetStatus("没有可展示的建议", "#554D3B1D", "#99D9A441");
					FillStateMessage(
						AdvisorUserMessages.ResponseStatus(
							status, response.Message, response.IsFinal, false),
						"#FFFFD58A");
				}
				FillWarnings(response.Warnings);
				return;
			}
			if (recommendations.Count == 0)
			{
				SetStatus(
					"战术模型暂未覆盖完整动作 · 可查看历史打法参考",
					"#554D3B1D",
					"#99D9A441");
				FillStateMessage(
					"当前没有可可靠展示的战术行动线；下方仅列出你过去的选择倾向。",
					"#FFFFD58A");
				FillBehaviorReferences(response.BehaviorReferences);
				FillWarnings((response.Warnings ?? new List<string>())
					.Concat(BuildOrderingModelNotices(response.Coverage)));
				return;
			}

			var isPartial = status == "partial" || status == "thinking" || !response.IsFinal;
			var hasProvenLethal = recommendations.Any(item => item.IsProvenLethal);
			var allResponsesVerified = recommendations.All(item =>
				item.IsProvenLethal || item.IsResponseVerified);
			var rootCoverageInvalid = response.Coverage != null &&
				response.Coverage.HasRootActionCoverageContract &&
				!response.Coverage.RootActionCoverageContractValid;
			var rootCoverageIncomplete = response.Coverage != null &&
				response.Coverage.HasRootActionCoverageContract &&
				response.Coverage.RootActionCoverageContractValid &&
				!response.Coverage.RootActionCoverageComplete;
			var boundedPortfolio = response.Coverage != null &&
				response.Coverage.HasRootActionCoverageContract &&
				response.Coverage.RootActionCoverageComplete &&
				!response.Coverage.PortfolioOptimalityProven;
			SetStatus(
				BuildSolveStatusText(
					status,
					response.IsFinal,
					hasProvenLethal,
					allResponsesVerified,
					rootCoverageInvalid,
					rootCoverageIncomplete,
					boundedPortfolio),
				hasProvenLethal
					? "#55523B22"
					: isPartial || rootCoverageInvalid || rootCoverageIncomplete || !allResponsesVerified
						? "#554D3B1D" : "#55305642",
				hasProvenLethal
					? "#99E1B04D"
					: isPartial || rootCoverageInvalid || rootCoverageIncomplete || !allResponsesVerified
						? "#99D9A441" : "#9970C997");
			FillRecommendations(recommendations);
			FillBehaviorReferences(showBehaviorReferences
				? response.BehaviorReferences : null);
			var portfolioCoverageNotice = rootCoverageInvalid || rootCoverageIncomplete
				? AdvisorUserMessages.PortfolioCoverageSummary(response.Coverage)
				: "";
			FillWarnings(
				(string.IsNullOrWhiteSpace(portfolioCoverageNotice)
						? Enumerable.Empty<string>()
						: new[] { portfolioCoverageNotice })
					.Concat(response.Warnings ?? new List<string>())
					.Concat(BuildOrderingModelNotices(response.Coverage))
					.Concat(recommendations.SelectMany(item => item.Risks ?? new List<string>()))
					.Concat(recommendations.SelectMany(
						item => item.ApproximateEffects ?? new List<string>())));
		}

		private void FillRecommendations(IEnumerable<AdvisorRecommendation> recommendations)
		{
			_recommendations.Children.Clear();
			_behaviorReferences.Children.Clear();
			_warnings.Children.Clear();
			var displayRank = 0;
			foreach (var recommendation in recommendations)
			{
				displayRank++;
				_recommendations.Children.Add(BuildRecommendation(recommendation, displayRank));
			}
		}

		private UIElement BuildRecommendation(AdvisorRecommendation recommendation, int displayRank)
		{
			var container = new Border
			{
				Background = Brush(displayRank == 1 ? "#54273746" : "#3A25313E"),
				BorderBrush = Brush(displayRank == 1 ? "#887CB6E8" : "#55708396"),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(4),
				Padding = new Thickness(7, 5, 7, 5),
				Margin = new Thickness(0, 0, 0, 5),
				ToolTip = BuildRecommendationToolTip(recommendation)
			};
			var content = new StackPanel();
			container.Child = content;

			var heading = new Grid();
			heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			content.Children.Add(heading);

			var rankText = new TextBlock
			{
				Text = BuildPriorityLabel(displayRank),
				Foreground = Brush(displayRank == 1 ? "#FF9DD2FF" : "#FFB9C9D8"),
				FontSize = 11.5,
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(0, 0, 7, 0),
				VerticalAlignment = VerticalAlignment.Center
			};
			Grid.SetColumn(rankText, 0);
			heading.Children.Add(rankText);

			var badge = new TextBlock
			{
				Text = BuildRecommendationBadge(recommendation),
				Foreground = recommendation.IsResponseVerified && recommendation.ResponseIsProvenLethal
					? Brush("#FFFF8C9D")
					: Brush("#FFFFD27A"),
				FontSize = 10.5,
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(8, 0, 0, 0),
				VerticalAlignment = VerticalAlignment.Center
			};
			Grid.SetColumn(badge, 2);
			heading.Children.Add(badge);

			content.Children.Add(BuildActionFlow(recommendation.Actions, _gameState));

			return container;
		}

		private static UIElement BuildActionFlow(
			IEnumerable<AdvisorAction> actions,
			AdvisorGameState gameState)
		{
			var flow = new WrapPanel
			{
				Margin = new Thickness(0, 4, 0, 0),
				VerticalAlignment = VerticalAlignment.Center
			};
			var normalized = (actions ?? Enumerable.Empty<AdvisorAction>())
				.Where(action => action != null)
				.OrderBy(action => action.Index <= 0 ? Int32.MaxValue : action.Index)
				.ToList();
			if (normalized.Count == 0)
				normalized.Add(new AdvisorAction { Type = "end_turn" });

			for (var index = 0; index < normalized.Count; index++)
			{
				var group = new StackPanel
				{
					Orientation = Orientation.Horizontal,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(0, 1, 3, 1)
				};
				if (index > 0)
				{
					group.Children.Add(new TextBlock
					{
						Text = "›",
						Foreground = Brush("#FF7890A5"),
						FontSize = 16,
						FontWeight = FontWeights.Bold,
						VerticalAlignment = VerticalAlignment.Center,
						Margin = new Thickness(0, 0, 3, 0)
					});
				}
				group.Children.Add(BuildActionChip(normalized[index], gameState));
				flow.Children.Add(group);
			}
			return flow;
		}

		private static UIElement BuildActionChip(AdvisorAction action, AdvisorGameState gameState)
		{
			var type = (action?.Type ?? "").Trim().ToLowerInvariant();
			var border = new Border
			{
				Background = Brush("#5C1C2732"),
				BorderBrush = Brush("#556A7C8E"),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(4),
				Padding = new Thickness(4, 2, 5, 2),
				ToolTip = AdvisorUserMessages.Action(action, gameState)
			};
			var row = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				VerticalAlignment = VerticalAlignment.Center
			};
			border.Child = row;

			var source = AdvisorUserMessages.FindEntity(gameState, action?.SourceEntityId);
			var target = AdvisorUserMessages.FindEntity(gameState, action?.TargetEntityId);
			if (target == null && type == "hero_power")
				target = FindAutomaticHeroTarget(gameState, source);

			if (type == "attack")
			{
				row.Children.Add(BuildEntityToken(gameState, source, action?.CardId, "攻击者", "攻"));
				row.Children.Add(BuildOperationGlyph("⚔", "#FFFFC46B"));
				row.Children.Add(BuildEntityToken(gameState, target, "", "目标", "敌"));
				return border;
			}

			row.Children.Add(BuildKindBadge(type));
			if (type == "end_turn" || type == "end turn" || type == "pass")
			{
				row.Children.Add(BuildTokenText("结束回合", 72));
				return border;
			}

			row.Children.Add(BuildEntityToken(
				gameState,
				source,
				action?.CardId,
				type == "hero_power" ? "英雄技能" : type == "location_activate" ? "地标" : "卡牌",
				type == "hero_power" ? "技" : type == "location_activate" ? "点" : "牌"));
			if (target != null || action?.TargetEntityId.HasValue == true)
			{
				row.Children.Add(BuildOperationGlyph("→", "#FF8EABC4"));
				row.Children.Add(BuildEntityToken(gameState, target, "", "目标", "敌"));
			}
			return border;
		}

		private static UIElement BuildKindBadge(string type)
		{
			string text;
			string color;
			switch ((type ?? "").Trim().ToLowerInvariant())
			{
				case "play_card":
				case "play":
					text = "出";
					color = "#FF74C69D";
					break;
				case "hero_power":
					text = "技";
					color = "#FFC69AF2";
					break;
				case "location_activate":
					text = "点";
					color = "#FF67C6D4";
					break;
				case "end_turn":
				case "end turn":
				case "pass":
					text = "停";
					color = "#FF9DAAB6";
					break;
				default:
					text = "做";
					color = "#FF8EABC4";
					break;
			}
			return new Border
			{
				Width = 22,
				Height = 22,
				CornerRadius = new CornerRadius(11),
				Background = Brush("#66374450"),
				BorderBrush = Brush(color),
				BorderThickness = new Thickness(1),
				Margin = new Thickness(0, 0, 4, 0),
				Child = new TextBlock
				{
					Text = text,
					Foreground = Brush(color),
					FontSize = 10,
					FontWeight = FontWeights.Bold,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				}
			};
		}

		private static UIElement BuildOperationGlyph(string glyph, string color)
		{
			return new TextBlock
			{
				Text = glyph,
				Foreground = Brush(color),
				FontSize = 13,
				FontWeight = FontWeights.Bold,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(4, 0, 4, 0)
			};
		}

		private static UIElement BuildEntityToken(
			AdvisorGameState gameState,
			AdvisorEntityState entity,
			string fallbackCardId,
			string fallbackLabel,
			string fallbackGlyph)
		{
			var cardId = !string.IsNullOrWhiteSpace(entity?.CardId)
				? entity.CardId
				: fallbackCardId ?? "";
			var label = BuildCompactEntityLabel(gameState, entity, cardId, fallbackLabel);
			var token = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				VerticalAlignment = VerticalAlignment.Center
			};
			var art = LoadCardArt(cardId);
			var artBorder = new Border
			{
				Width = CardArtWidth,
				Height = CardArtHeight,
				CornerRadius = new CornerRadius(3),
				BorderBrush = Brush("#668CA0B3"),
				BorderThickness = new Thickness(1),
				Background = art == null
					? Brush("#AA304052")
					: new ImageBrush(art)
					{
						Stretch = Stretch.UniformToFill,
						AlignmentX = AlignmentX.Center,
						AlignmentY = AlignmentY.Center
					},
				ToolTip = label
			};
			if (art == null)
			{
				artBorder.Child = new TextBlock
				{
					Text = string.IsNullOrWhiteSpace(fallbackGlyph) ? "?" : fallbackGlyph,
					Foreground = Brush("#FFD5E2ED"),
					FontSize = 10,
					FontWeight = FontWeights.Bold,
					HorizontalAlignment = HorizontalAlignment.Center,
					VerticalAlignment = VerticalAlignment.Center
				};
			}
			token.Children.Add(artBorder);
			var labelText = BuildTokenText(label, 82);
			labelText.Margin = new Thickness(4, 0, 0, 0);
			token.Children.Add(labelText);
			return token;
		}

		private static TextBlock BuildTokenText(string text, double width)
		{
			return new TextBlock
			{
				Text = string.IsNullOrWhiteSpace(text) ? "目标" : text,
				Foreground = Brush("#FFE7EEF5"),
				FontSize = 10.5,
				FontWeight = FontWeights.SemiBold,
				Width = width,
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center,
				ToolTip = text ?? ""
			};
		}

		internal static string BuildCompactEntityLabel(
			AdvisorGameState gameState,
			AdvisorEntityState entity,
			string fallbackCardId,
			string fallbackLabel)
		{
			if (entity == null)
			{
				var localized = AdvisorUserMessages.ResolveLocalizedCardName(fallbackCardId);
				return string.IsNullOrWhiteSpace(localized) ? fallbackLabel ?? "目标" : localized;
			}

			var owner = FindOwner(gameState, entity.EntityId);
			var side = owner == null ? "" : ReferenceEquals(owner, gameState?.Player) ? "我" : "敌";
			if (owner?.Hero != null && owner.Hero.EntityId == entity.EntityId)
				return side + "方英雄";
			if (owner?.HeroPower != null && owner.HeroPower.EntityId == entity.EntityId)
				return side + "方技能";
			if (owner?.Weapon != null && owner.Weapon.EntityId == entity.EntityId)
				return side + "方武器";

			var entityCardId = !string.IsNullOrWhiteSpace(entity.CardId)
				? entity.CardId
				: fallbackCardId;
			var localizedName = AdvisorUserMessages.ResolveLocalizedCardName(entityCardId);
			if (string.IsNullOrWhiteSpace(localizedName) && !string.IsNullOrWhiteSpace(entity.Name))
				localizedName = entity.Name.Trim();
			if (string.IsNullOrWhiteSpace(localizedName))
				localizedName = fallbackLabel ?? "目标";
			var position = BoardPosition(owner, entity.EntityId);
			return position > 0
				? side + position.ToString(CultureInfo.InvariantCulture) + "·" + localizedName
				: localizedName;
		}

		private static AdvisorPlayerState FindOwner(AdvisorGameState gameState, int entityId)
		{
			if (gameState == null || entityId <= 0)
				return null;
			if (AdvisorUserMessages.FindEntity(gameState.Player, entityId) != null)
				return gameState.Player;
			return AdvisorUserMessages.FindEntity(gameState.Opponent, entityId) != null
				? gameState.Opponent
				: null;
		}

		private static int BoardPosition(AdvisorPlayerState owner, int entityId)
		{
			if (owner?.Board == null)
				return 0;
			for (var index = 0; index < owner.Board.Count; index++)
			{
				var item = owner.Board[index];
				if (item == null || item.EntityId != entityId)
					continue;
				return item.ZonePosition >= 1 && item.ZonePosition <= 7
					? item.ZonePosition
					: index + 1;
			}
			return 0;
		}

		private static AdvisorEntityState FindAutomaticHeroTarget(
			AdvisorGameState gameState,
			AdvisorEntityState source)
		{
			if (gameState == null || source == null)
				return null;
			var text = ((source.EnglishText ?? "") + " " + (source.CardText ?? ""))
				.ToLowerInvariant();
			var enemy = text.Contains("enemy hero") || text.Contains("敌方英雄");
			var friendly = text.Contains("your hero") || text.Contains("你的英雄") ||
				text.Contains("己方英雄");
			if (enemy == friendly)
				return null;
			return enemy ? gameState.Opponent?.Hero : gameState.Player?.Hero;
		}

		internal static string FindCardArtPath(string cardId, string applicationData = null)
		{
			var normalized = (cardId ?? "").Trim();
			if (string.IsNullOrWhiteSpace(normalized) ||
				!string.Equals(Path.GetFileName(normalized), normalized, StringComparison.Ordinal))
				return "";
			var root = Path.Combine(
				applicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"HearthstoneDeckTracker",
				"Images");
			foreach (var relative in new[]
			{
				Path.Combine("CardTiles", normalized + ".jpg"),
				Path.Combine("CardPortraits", normalized + ".jpg"),
				Path.Combine("CardImages", normalized + ".png")
			})
			{
				var path = Path.Combine(root, relative);
				if (File.Exists(path))
					return path;
			}
			return "";
		}

		private static ImageSource LoadCardArt(string cardId)
		{
			var normalized = (cardId ?? "").Trim();
			if (string.IsNullOrWhiteSpace(normalized))
				return null;
			lock (CardArtCacheLock)
			{
				ImageSource cached;
				if (CardArtCache.TryGetValue(normalized, out cached))
					return cached;
				try
				{
					var path = FindCardArtPath(normalized);
					if (string.IsNullOrWhiteSpace(path))
					{
						CardArtCache[normalized] = null;
						return null;
					}
					var bitmap = new BitmapImage();
					bitmap.BeginInit();
					bitmap.CacheOption = BitmapCacheOption.OnLoad;
					bitmap.DecodePixelWidth = 128;
					bitmap.UriSource = new Uri(path, UriKind.Absolute);
					bitmap.EndInit();
					bitmap.Freeze();
					CardArtCache[normalized] = bitmap;
					return bitmap;
				}
				catch (Exception ex)
				{
					CardArtCache[normalized] = null;
					Log.Info("建议面板未能加载本地卡图，已使用文字图标：" +
						ex.GetType().Name);
					return null;
				}
			}
		}

		internal static string BuildPriorityLabel(int displayRank)
		{
			if (displayRank <= 1)
				return "首选";
			if (displayRank == 2)
				return "备选一";
			if (displayRank == 3)
				return "备选二";
			return "备选" + (displayRank - 1).ToString(CultureInfo.InvariantCulture);
		}

		internal static string BuildRecommendationBadge(AdvisorRecommendation recommendation)
		{
			if (recommendation == null)
				return "";
			if (recommendation.IsProvenLethal)
				return "斩杀";
			return recommendation.IsResponseVerified && recommendation.ResponseIsProvenLethal
				? "有反杀"
				: "";
		}

		private void FillBehaviorReferences(AdvisorBehaviorReferenceSet references)
		{
			_behaviorReferences.Children.Clear();
			if (references == null || !references.IsDisplayEligible)
				return;

			var container = new Border
			{
				Background = Brush("#44342A45"),
				BorderBrush = Brush("#777A638E"),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(4),
				Padding = new Thickness(8, 7, 8, 7),
				Margin = new Thickness(0, 1, 0, 7),
				ToolTip = BehaviorReferenceDisclosure
			};
			var content = new StackPanel();
			container.Child = content;
			content.Children.Add(new TextBlock
			{
				Text = BehaviorReferenceHeading,
				Foreground = Brush("#FFE2C8F3"),
				FontSize = 11.5,
				FontWeight = FontWeights.SemiBold,
				TextWrapping = TextWrapping.Wrap
			});
			content.Children.Add(new TextBlock
			{
				Text = BehaviorReferenceDisclosure,
				Foreground = Brush("#FFB8A8C4"),
				FontSize = 9.8,
				Margin = new Thickness(0, 2, 0, 4),
				TextWrapping = TextWrapping.Wrap
			});
			foreach (var reference in (references.References ??
				new List<AdvisorBehaviorReference>()).OrderBy(item => item.Rank))
			{
				content.Children.Add(new TextBlock
				{
					Text = BuildBehaviorReferenceLine(reference),
					Foreground = Brush("#FFE9E0EF"),
					FontSize = 10.8,
					Margin = new Thickness(0, 2, 0, 0),
					TextWrapping = TextWrapping.Wrap
				});
			}
			_behaviorReferences.Children.Add(container);
		}

		private void FillStateMessage(string message, string color)
		{
			_recommendations.Children.Clear();
			_behaviorReferences.Children.Clear();
			_warnings.Children.Clear();
			_recommendations.Children.Add(new TextBlock
			{
				Text = message ?? "",
				Foreground = Brush(color),
				FontSize = 11,
				Margin = new Thickness(1, 2, 1, 3),
				TextWrapping = TextWrapping.Wrap
			});
		}

		private void FillWarnings(IEnumerable<string> warnings)
		{
			_warnings.Children.Clear();
			var normalized = AdvisorUserMessages.Notices(warnings);
			if (normalized.Count == 0)
			{
				return;
			}

			_warnings.Children.Add(new TextBlock
			{
				Text = "⚠ " + normalized.Count.ToString(CultureInfo.InvariantCulture) +
					" 项模型限制",
				Foreground = Brush("#FFFFC46B"),
				FontSize = 10,
				TextWrapping = TextWrapping.NoWrap,
				TextTrimming = TextTrimming.CharacterEllipsis,
				ToolTip = WarningHeading + string.Join("；", normalized.ToArray())
			});
		}

		private void UpdateSubtitle(AdvisorCoverage coverage = null)
		{
			_subtitle.Text = "本地计算 · 仅提供建议";
			_subtitle.ToolTip = BuildOrderingModelSubtitle(coverage) +
				Environment.NewLine + GeneralToolTip;
		}

		internal static string BuildOrderingModelSubtitle(AdvisorCoverage coverage)
		{
			var parts = new List<string> { "本地计算" };
			if (IsSafeOrderingApplied(coverage?.DecisionRanker, true))
				parts.Add("本方决策排序已参与");
			if (IsSafeOrderingApplied(coverage?.BehaviorPrior, false))
				parts.Add("对手行为先验已参与");
			parts.Add("仅提供建议，不会自动操作游戏");
			return string.Join(" · ", parts.ToArray());
		}

		internal static List<string> BuildOrderingModelNotices(AdvisorCoverage coverage)
		{
			var notices = new List<string>();
			AppendOrderingModelNotice(
				notices,
				coverage?.DecisionRanker,
				true,
				"本方决策排序器本局已安全停用，继续使用基础搜索顺序。");
			AppendOrderingModelNotice(
				notices,
				coverage?.BehaviorPrior,
				false,
				"对手行为先验本局已安全停用，继续使用基础搜索顺序。");
			return notices;
		}

		internal static bool ShouldDisplayBehaviorReferences(
			string status, AdvisorBehaviorReferenceSet references)
		{
			return string.Equals(
				(status ?? "").Trim(), AdvisorProtocol.StatusPartial,
				StringComparison.OrdinalIgnoreCase) &&
				references != null && references.IsDisplayEligible && references.Available &&
				references.CandidateSetComplete && references.CandidateCount > 0 &&
				references.RankedCandidateCount == references.CandidateCount &&
				references.DisplayedReferenceCount > 0 &&
				references.DisplayedReferenceCount ==
					(references.References ?? new List<AdvisorBehaviorReference>()).Count &&
				references.BehaviorReferenceEligible &&
				!references.CandidateGenerationAllowed &&
				!references.TacticalScoreOverrideAllowed &&
				!references.AutomaticActionAllowed &&
				!references.LivePolicyEligible && !references.RlTrainingEligible &&
				!references.OptimalityVerified &&
				!references.OutcomeUsedAsActionOptimality;
		}

		internal static string BuildBehaviorReferenceLine(AdvisorBehaviorReference reference)
		{
			if (reference == null)
				return "";
			return "#" + Math.Max(1, reference.Rank).ToString(CultureInfo.InvariantCulture) +
					" " + AdvisorUserMessages.Action(reference.Action) +
				" · 历史选择倾向 " + FormatPercent(reference.ObservedChoiceProbability) +
				"（不是胜率）";
		}

		private static void AppendOrderingModelNotice(
			ICollection<string> notices,
			AdvisorSearchOrderingStatus model,
			bool requireLocalActionsOnly,
			string rejectionMessage)
		{
			if (model == null)
				return;
			var status = (model.Status ?? "").Trim().ToLowerInvariant();
			if (status == "runtime_rejected")
			{
				notices.Add(rejectionMessage);
				return;
			}
			if (status == "applied" &&
				!IsSafeOrderingApplied(model, requireLocalActionsOnly))
			{
				notices.Add("模型排序状态校验未通过，当前仅按基础求解结果展示。");
			}
		}

		private static bool IsSafeOrderingApplied(
			AdvisorSearchOrderingStatus model,
			bool requireLocalActionsOnly)
		{
			return model != null && model.OrderingApplied &&
				string.Equals(model.Status, "applied", StringComparison.OrdinalIgnoreCase) &&
				model.OrderingAttemptCount > 0 && model.SearchOrderingOnly &&
				(!requireLocalActionsOnly || model.LocalActionsOnly) &&
				!model.CandidateGenerationAllowed && !model.ScoreOverrideAllowed &&
				!model.LivePolicyEligible && !model.RlTrainingEligible &&
				!model.OptimalityVerified;
		}

		private void SetStatus(string text, string background, string border)
		{
			_statusText.Text = text ?? "";
			_statusBorder.Background = Brush(background);
			_statusBorder.BorderBrush = Brush(border);
			_statusBorder.BorderThickness = new Thickness(1);
		}

		internal static string BuildSolveStatusText(
			string status,
			bool isFinal,
			bool hasProvenLethal,
			bool allResponsesVerified,
			bool rootCoverageInvalid,
			bool rootCoverageIncomplete,
			bool boundedPortfolio)
		{
			var normalizedStatus = (status ?? "").Trim().ToLowerInvariant();
			if (hasProvenLethal)
				return "发现斩杀 · 受支持的公开通用规则内已证明";
			if (normalizedStatus == "thinking" || !isFinal)
				return "搜索中 · 对手回应尚未全部验证";
			if (normalizedStatus == "partial")
				return AdvisorUserMessages.FinalPartialStatus;
			if (rootCoverageInvalid)
				return "计算完成 · 合法首步覆盖信息不一致";
			if (rootCoverageIncomplete)
				return "计算完成 · 合法首步尚未全部验证";
			if (boundedPortfolio && allResponsesVerified)
				return "计算完成 · 合法首步已覆盖，展示当前最佳";
			return allResponsesVerified
				? "计算完成 · 对手可见最坏回应已验证"
				: "计算完成 · 部分路线的对手回应未验证";
		}

		internal static string BuildMetrics(AdvisorRecommendation recommendation)
		{
			var metrics = new List<string>();
			var alternativeKind = AdvisorUserMessages.AlternativeKind(
				recommendation.AlternativeKind);
			if (!string.IsNullOrWhiteSpace(alternativeKind))
				metrics.Add(alternativeKind);
			if ((recommendation.IsProvenLethal || recommendation.IsResponseVerified) &&
				recommendation.VerifiedPortfolioRegret.HasValue)
			{
				metrics.Add(Math.Abs(recommendation.VerifiedPortfolioRegret.Value) <= 0.000001
					? "与已验证最佳并列"
					: "与已验证最佳的战术值差距 " +
						FormatTacticalValue(recommendation.VerifiedPortfolioRegret.Value));
			}
			if (recommendation.IsProvenLethal)
			{
				metrics.Add("受支持公开规则内已证明斩杀");
				if (!string.IsNullOrWhiteSpace(recommendation.ProofScope))
					metrics.Add("证明范围：" + AdvisorUserMessages.Scope(recommendation.ProofScope));
			}
			else
			{
				metrics.Add(recommendation.IsResponseVerified
					? "对手最坏可见回应后战术评分 " + FormatPercent(
						recommendation.WorstCaseScore ?? recommendation.ExpectedWinRate)
					: "当前战术评分（回应未验证）" + FormatPercent(
						recommendation.WorstCaseScore ?? recommendation.ExpectedWinRate));
				metrics.Add("未校准为真实胜率");
				if (recommendation.IsResponseVerified && recommendation.MinimaxValue.HasValue)
					metrics.Add("最差回应战术值 " + FormatTacticalValue(recommendation.MinimaxValue.Value));
				if (recommendation.IsResponseVerified && recommendation.ResponseIsProvenLethal)
					metrics.Add("对手在受支持公开规则内有斩杀回应");
				else if (recommendation.IsResponseVerified)
					metrics.Add("对手回应已验证 · " + AdvisorUserMessages.Scope(recommendation.ResponseScope));
				else if (!string.IsNullOrWhiteSpace(recommendation.ResponseScope))
					metrics.Add("对手回应未完整验证（可能受时间、节点或契约校验限制）");
				else
					metrics.Add("未获得可信的对手回应验证");
			}
			if (recommendation.Confidence.HasValue)
			{
				metrics.Add("搜索稳定度 " + FormatPercent(recommendation.Confidence.Value));
			}
			if (!recommendation.IsProvenLethal && recommendation.WinRateLow.HasValue && recommendation.WinRateHigh.HasValue)
			{
				metrics.Add(
					"启发式区间 " + FormatPercent(recommendation.WinRateLow.Value) +
					"–" + FormatPercent(recommendation.WinRateHigh.Value));
			}
			if (recommendation.Visits > 0)
			{
				metrics.Add(recommendation.Visits.ToString("N0", CultureInfo.InvariantCulture) + " 次搜索");
			}
			return string.Join(" · ", metrics.ToArray());
		}

		internal static string BuildOpponentResponsePrefix(AdvisorRecommendation recommendation)
		{
			return recommendation != null && recommendation.IsResponseVerified
				? "对手最坏可见回应："
				: "对手已搜索回应（未验证）：";
		}

		internal static string BuildActionLine(IEnumerable<AdvisorAction> actions)
		{
			return BuildActionLine(actions, null);
		}

		internal static string BuildActionLine(
			IEnumerable<AdvisorAction> actions,
			AdvisorGameState gameState)
		{
			var normalized = (actions ?? Enumerable.Empty<AdvisorAction>())
				.Where(action => action != null)
				.OrderBy(action => action.Index <= 0 ? Int32.MaxValue : action.Index)
				.Select(action => AdvisorUserMessages.Action(action, gameState))
				.Where(text => !string.IsNullOrWhiteSpace(text))
				.ToList();
			return normalized.Count == 0
				? "没有返回可执行动作（可能是结束回合）"
				: string.Join("  →  ", normalized.ToArray());
		}

		private string BuildRecommendationToolTip(AdvisorRecommendation recommendation)
		{
			var lines = new List<string>
			{
				"说明：" + AdvisorUserMessages.RecommendationSummary(recommendation),
				"完整行动线：" + BuildActionLine(recommendation.Actions, _gameState),
				BuildMetrics(recommendation)
			};
			if (recommendation.OpponentReply != null && recommendation.OpponentReply.Count > 0)
				lines.Add(BuildOpponentResponsePrefix(recommendation) +
					BuildActionLine(recommendation.OpponentReply, _gameState));
			var risks = AdvisorUserMessages.Notices(recommendation.Risks);
			if (risks.Count > 0)
			{
				lines.Add("风险：" + string.Join("；", risks.ToArray()));
			}
			var approximate = AdvisorUserMessages.Notices(
				recommendation.ApproximateEffects, approximationOnly: true);
			if (approximate.Count > 0)
			{
				lines.Add("近似机制：" + string.Join("；", approximate.ToArray()));
			}
			return string.Join(Environment.NewLine, lines.ToArray());
		}

		private static string FormatPercent(double value)
		{
			if (Double.IsNaN(value) || Double.IsInfinity(value))
			{
				return "--";
			}
			var normalized = Math.Max(0, Math.Min(1, value));
			return normalized.ToString("P1", CultureInfo.InvariantCulture);
		}

		private static string FormatTacticalValue(double value)
		{
			if (Double.IsNaN(value) || Double.IsInfinity(value))
				return "--";
			return value.ToString("0.###", CultureInfo.InvariantCulture);
		}

		private static Brush WinRateBrush(double winRate)
		{
			if (winRate >= .6)
			{
				return Brush("#FF77D39B");
			}
			if (winRate < .4)
			{
				return Brush("#FFFF8C9D");
			}
			return Brush("#FFFFD27A");
		}

		private static Brush Brush(string color)
		{
			return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
		}
	}
}
