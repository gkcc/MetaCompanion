#region

using Hearthstone_Deck_Tracker.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Core = Hearthstone_Deck_Tracker.API.Core;

#endregion

namespace MetaCompanion
{
	public partial class PredictionLayout
	{
		private const int CardHeight = 32;
		private const double BottomFromScreenRatio = .2;
		private const double LeftFromScreenRatio = .005;
		private const double FittedMaxHeightRatio = .55;
		private const double LateGameTopFromScreenRatio = .075;
		private const double LateGameSideFromScreenRatio = .012;
		private const double LateGameMaxHeightRatio = .38;
		private const double DefaultPanelWidth = 305;
		private const double LateGamePanelWidth = 245;
		private const double DefaultCardListWidth = 221;
		private const double LateGameCardListWidth = 243;
		private const double AbsoluteMaxHeightRatio = 1 - BottomFromScreenRatio;
		private readonly PluginConfig _config;

		public PredictionLayout(PluginConfig config)
		{
			_config = config;
			InitializeComponent();
			OverlayDragHelper.Enable(this, HeaderBar, SaveLateGamePanelPosition);
		}

		public void UpdateCardToolTip(Point mousePos)
		{
			if (CardList.AnimatedCards.Count == 0)
			{
				CardToolTip.Visibility = Visibility.Collapsed;
				return;
			}

			Point relativePos = CardView.PointFromScreen(mousePos);
			bool mouseInsideCardList = relativePos.X > 0 && relativePos.X < CardView.ActualWidth &&
				relativePos.Y > 0 && relativePos.Y < CardView.ActualHeight;
			if (!mouseInsideCardList)
			{
				CardToolTip.Visibility = Visibility.Collapsed;
				return;
			}

			var cardSize = CardView.ActualHeight / CardList.AnimatedCards.Count;
			var cardIndex = (int)(relativePos.Y / cardSize);
			if (cardIndex < 0 || cardIndex >= CardList.AnimatedCards.Count)
			{
				CardToolTip.Visibility = Visibility.Collapsed;
				return;
			}

			CardToolTip.SetValue(DataContextProperty, CardList.AnimatedCards.ElementAt(cardIndex).Card);
			Canvas.SetTop(CardToolTip, cardSize * cardIndex);
			CardToolTip.Visibility = Visibility.Visible;
		}

		public void Update(PredictionInfo prediction, bool lateGameMode = false)
		{
			var sourceCards = lateGameMode && prediction.ClosestDeckRemainingCards.Count > 0
				? prediction.ClosestDeckRemainingCards
				: prediction.PredictedCards;
			var allVisiblePredictions = sourceCards
				.Where(cardInfo => cardInfo.UnplayedCount > 0)
				.ToList();
			var visiblePredictions = lateGameMode
				? allVisiblePredictions
					.Take(Math.Max(1, _config.LateGamePanelCardLimit))
					.ToList()
				: allVisiblePredictions;
			var cards = visiblePredictions
				.GroupBy(cardInfo => cardInfo.Card.Id, cardInfo => cardInfo.GetCardWithUnplayedCount())
				.Select(group => group.Reverse())
				.SelectMany(x => x)
				.ToList();

			ApplyModeLayout(lateGameMode);
			bool hasEvidence = prediction.EvidenceCards > 0;
			bool hasPrediction = hasEvidence && cards.Count > 0 && prediction.NumPossibleDecks > 0;
			CardList.Update(
				hasPrediction ? cards : new List<Hearthstone_Deck_Tracker.Hearthstone.Card>(), true);
			CardList.Opacity = .62;
			CardView.Visibility = hasPrediction ? Visibility.Visible : Visibility.Collapsed;
			EmptyState.Visibility = hasPrediction ? Visibility.Collapsed : Visibility.Visible;
			UpdateStatus(prediction, lateGameMode, hasEvidence, cards.Count);
			UpdatePercentages(visiblePredictions);
			UpdateStats(
				prediction,
				lateGameMode,
				hasEvidence,
				cards.Count,
				allVisiblePredictions.Sum(cardInfo => cardInfo.UnplayedCount));
			FitToOverlay(cards.Count, lateGameMode);
		}

		private void UpdateStatus(
			PredictionInfo prediction, bool lateGameMode, bool hasEvidence, int cardCount)
		{
			if (prediction.NumPossibleDecks == 0)
			{
				StatusText.Text = "无匹配";
				StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
					System.Windows.Media.Color.FromRgb(244, 167, 95));
				EmptyStateText.Text = "该职业暂无候选牌组";
				return;
			}

			if (!hasEvidence)
			{
				StatusText.Text = "职业已知";
				StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
					System.Windows.Media.Color.FromRgb(164, 184, 204));
				EmptyStateText.Text = "等待第一张对手原始牌";
				return;
			}

			if (cardCount == 0)
			{
				StatusText.Text = "等待证据";
				StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
					System.Windows.Media.Color.FromRgb(164, 184, 204));
				EmptyStateText.Text = "等待更多可用于预测的牌";
				return;
			}

			StatusText.Text = lateGameMode ? "预测未见牌" :
				(prediction.NumPossibleDecks == 1 ? "基本锁定" : "筛选中");
			StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
				System.Windows.Media.Color.FromRgb(104, 211, 145));
			StatusText.ToolTip = lateGameMode
				? "显示对方构筑里可能携带、但目前还没打出的牌；使用独立浮窗避免遮挡 HDT 原生牌表。"
				: "根据已确认原始牌筛选候选分支，并把未见牌同步到 HDT 原生对手牌表。";
		}

		private void UpdatePercentages(List<PredictionInfo.CardInfo> visiblePredictions)
		{
			PercentageList.ItemsSource = visiblePredictions.Select(cardInfo =>
				{
					var nextProbabilities = cardInfo.Probabilities.Skip(cardInfo.NumPlayed).ToList();
					bool alreadyPlayed = (cardInfo.Card.Count - cardInfo.NumPlayed <= 0);
					return new PercentageItem(
						nextProbabilities, cardInfo.Playability, alreadyPlayed, cardInfo.OffMeta);
				}).ToList();
		}

		private void UpdateStats(
			PredictionInfo prediction, bool lateGameMode, bool hasEvidence, int cardCount,
			int totalUnplayedCount)
		{
			var remainingDeckText = prediction.RemainingDeckCards.HasValue
				? " / 牌库 " + prediction.RemainingDeckCards.Value
				: "";
			var lateGameCardText = totalUnplayedCount > cardCount
				? " / 显示 " + cardCount + "/" + totalUnplayedCount
				: " / 未见 " + cardCount;
			PossibleCards.Text = lateGameMode
				? "已确认 " + prediction.EvidenceCards + remainingDeckText + lateGameCardText
				: (hasEvidence
					? "已见 " + prediction.EvidenceCards + " / 预测 " +
						prediction.NumVisiblePredictedCards + " / 可能 " + prediction.NumPossibleCards
					: "已见 " + prediction.EvidenceCards + " / 可能 " + prediction.NumPossibleCards);
			PossibleCards.ToolTip = lateGameMode
				? "已确认是对手原始牌证据；未见牌是最接近构筑分支中仍可能留在牌组里的牌。"
				: "已见是对手原始牌证据；预测是当前会显示在原生牌表中的未见牌。";
			PossibleDecks.Text = "候选分支 " + prediction.NumPossibleDecks;
			PossibleDecks.ToolTip = "候选分支数量来自本地牌组数据库中与对手职业和已见证据匹配的构筑。";
			var evidenceText = prediction.FormatKeyEvidence(3);
			var evidenceSuffix = string.IsNullOrWhiteSpace(evidenceText)
				? ""
				: " / 已见 " + evidenceText;
			ArchetypeText.Text = (lateGameMode && !string.IsNullOrEmpty(prediction.ClosestDeckName)
				? "基于最接近分支: " + prediction.ClosestDeckName + " / 置信 " +
					prediction.ConfidencePercent + "% " + prediction.ConfidenceLabel
				: (prediction.CandidateArchetypes.Count > 0
					? string.Join("  /  ", prediction.CandidateArchetypes.Take(2)
						.Select(candidate => candidate.Name + " " +
							candidate.ConfidencePercent + "%"))
					: "尚未识别构筑")) + evidenceSuffix;
			ArchetypeText.ToolTip = lateGameMode
				? "浮窗使用最接近分支列出未见牌；已见证据只用于解释和评分，不会再当成未见牌提醒。"
				: "形态置信度由已见原始牌与候选分支匹配度计算。";
		}

		private void ApplyModeLayout(bool lateGameMode)
		{
			RootBorder.Width = lateGameMode ? LateGamePanelWidth : DefaultPanelWidth;
			HeaderTitle.Text = lateGameMode ? "剩余卡牌预测" : "对手牌组预测";
			HeaderTitle.ToolTip = lateGameMode
				? "独立显示最接近构筑分支的未见牌，避免原生牌表被过多预测牌遮挡。"
				: "预测对手构筑中尚未出现的牌。";
			CardList.Width = lateGameMode ? LateGameCardListWidth : DefaultCardListWidth;
			PercentageList.Visibility = lateGameMode ? Visibility.Collapsed : Visibility.Visible;
		}

		private void FitToOverlay(int cardCount, bool lateGameMode)
		{
			Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			Arrange(new Rect(0, 0, DesiredSize.Width, DesiredSize.Height));

			double maxHeightRatio = lateGameMode
				? LateGameMaxHeightRatio
				: (_config.FitDeckListToDisplay ? FittedMaxHeightRatio : AbsoluteMaxHeightRatio);
			double displayHeight =
				Core.OverlayWindow.Height - System.Windows.SystemParameters.WindowCaptionHeight;
			double maxHeight = maxHeightRatio * displayHeight;
			if (cardCount * CardHeight > maxHeight - InfoBox.ActualHeight)
			{
				CardView.Height = maxHeight - InfoBox.ActualHeight;
			}
			else
			{
				CardView.Height = Double.NaN;
			}

			if (lateGameMode)
			{
				if (OverlayDragHelper.HasCustomPosition(this))
				{
					return;
				}

				if (_config.HasLateGamePanelPosition)
				{
					OverlayDragHelper.ApplyNormalizedPosition(
						this,
						_config.LateGamePanelPositionX,
						_config.LateGamePanelPositionY);
					return;
				}

				Canvas.SetBottom(this, Double.NaN);
				Canvas.SetTop(this, Core.OverlayWindow.Height * LateGameTopFromScreenRatio);
				if (_config.LateGamePanelRightSide)
				{
					Canvas.SetLeft(this, Double.NaN);
					Canvas.SetRight(this, Core.OverlayWindow.Width * LateGameSideFromScreenRatio);
				}
				else
				{
					Canvas.SetRight(this, Double.NaN);
					Canvas.SetLeft(this, Core.OverlayWindow.Width * LeftFromScreenRatio);
				}
				return;
			}

			Canvas.SetTop(this, Double.NaN);
			Canvas.SetRight(this, Double.NaN);
			Canvas.SetBottom(this, Core.OverlayWindow.Height * BottomFromScreenRatio);
			Canvas.SetLeft(this, Core.OverlayWindow.Width * LeftFromScreenRatio);
		}

		private void SaveLateGamePanelPosition(FrameworkElement target)
		{
			if (_config == null)
			{
				return;
			}

			var position = OverlayDragHelper.GetNormalizedPosition(target);
			_config.HasLateGamePanelPosition = true;
			_config.LateGamePanelPositionX = position.X;
			_config.LateGamePanelPositionY = position.Y;
			_config.Save();
		}

		public class PercentageItem
		{
			public PercentageItem(
				List<decimal> probabilities, PlayableType playability, bool alreadyPlayed, bool offMeta)
			{
				bool onlyShowFirst = true;
				if (probabilities.Count == 0)
				{
					Percentage = "";
				}
				else if (onlyShowFirst || probabilities.All(prob => prob == probabilities[0]))
				{
					Percentage = DecimalToPercent(probabilities[0]);
				}
				else
				{
					Percentage = String.Join(" / ", probabilities.Select(prob => DecimalToPercent(prob)));
				}

				bool showOptimal = (!alreadyPlayed && playability == PlayableType.AtAvailableMana);
				OptimalVisibility = showOptimal ? Visibility.Visible : Visibility.Collapsed;
				bool showCoin = (!alreadyPlayed && playability == PlayableType.AtAvailableManaWithCoin);
				CoinVisibility = showCoin ? Visibility.Visible : Visibility.Collapsed;
				bool showX = (offMeta && !showOptimal && !showCoin);
				XVisibility = showX ? Visibility.Visible : Visibility.Collapsed;
				ItemVisibility = (Percentage == "" && !showOptimal && !showCoin && !showX)
					? Visibility.Hidden : Visibility.Visible;
				ItemOpacity = ((playability == PlayableType.AboveAvailableMana || alreadyPlayed) ? .3 : 1);
			}

			public string Percentage { get; private set; }
			public double ItemOpacity { get; private set; }
			public Visibility ItemVisibility { get; private set; }
			public Visibility CoinVisibility { get; private set; }
			public Visibility OptimalVisibility { get; private set; }
			public Visibility XVisibility { get; private set; }
			public int CardHeight => PredictionLayout.CardHeight;

			private static string DecimalToPercent(decimal value) =>
				Math.Truncate(value * 100).ToString() + "%";
		}
	}
}

