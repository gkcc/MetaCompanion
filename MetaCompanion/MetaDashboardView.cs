using Hearthstone_Deck_Tracker.API;
using System;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MetaCompanion
{
	internal class MetaDashboardView
	{
		private const double TopRatio = .105;
		private const double RightRatio = .025;
		private const double MaxUsableSavedPositionY = .9;

		private readonly PluginConfig _config;
		private MetaDashboardPanel _panel;
		private DispatcherTimer _hideTimer;
		private bool _localSampleActionRunning;
		private string _localSampleActionStatus = "";

		public MetaDashboardView(PluginConfig config)
		{
			_config = config ?? new PluginConfig();
		}

		public bool UserDismissed { get; private set; }

		public void ResetUserDismissed()
		{
			UserDismissed = false;
		}

		public void ShowRecommendations()
		{
			Show("卡组流派推荐", TimeSpan.Zero);
		}

		public void ShowStandardStart()
		{
			ShowRecommendations();
		}

		public void ShowPostGame()
		{
			ShowRecommendations();
		}

		public void Hide()
		{
			RunOnOverlayThread(() =>
				{
					_hideTimer?.Stop();
					if (_panel != null)
					{
						_panel.Visibility = Visibility.Collapsed;
					}
				});
		}

		public void OnUnload()
		{
			RunOnOverlayThread(() =>
				{
					_hideTimer?.Stop();
					_hideTimer = null;
					var parent = _panel?.Parent as Panel;
					if (parent != null)
					{
						parent.Children.Remove(_panel);
					}
					OverlayInteractivityHelper.Unregister(_panel);
					_panel = null;
				});
		}

		private void Show(string title, TimeSpan duration)
		{
			try
				{
					RunOnOverlayThread(() =>
						{
							try
							{
								var shouldSyncLocalSampleState =
									ShouldSyncLocalSamplePanelState(_panel);
								if (!EnsurePanel())
								{
									return;
								}

								var snapshot = MetaDashboardSnapshot.Load(
									MetaCompanionPlugin.DataDirectory);
								_panel.Update(title, snapshot);
								if (shouldSyncLocalSampleState)
								{
									UpdateLocalSamplePanelState();
								}
								_panel.Visibility = Visibility.Visible;
								PositionPanel();
								RestartTimer(duration);
							}
							catch (Exception ex)
							{
								Log.Warn("Meta dashboard display failed: " + ex);
								ShowLoadFailure(title);
							}
						});
				}
			catch (Exception ex)
			{
				Log.Warn("Meta dashboard display scheduling failed: " + ex);
			}
		}

		internal static bool ShouldSyncLocalSamplePanelState(MetaDashboardPanel panel)
		{
			return panel == null ||
				panel.Visibility != Visibility.Visible ||
				!panel.HasPendingLocalSampleSelection;
		}

		private void ShowLoadFailure(string title)
		{
			if (_panel == null)
			{
				return;
			}

			try
			{
				_panel.ShowLoadFailure(title);
				_panel.Visibility = Visibility.Visible;
				PositionPanel();
			}
			catch (Exception ex)
			{
				Log.Warn("Meta dashboard failure status display failed: " + ex);
			}
		}

		private bool EnsurePanel()
		{
			var canvas = GetOverlayCanvas();
			if (canvas == null)
			{
				return false;
			}

			if (_panel == null)
			{
				_panel = new MetaDashboardPanel(
					HideByUser,
					ApplyLastGameCorrection,
					HandleLocalSampleAction);
				UpdateLocalSamplePanelState();
				OverlayDragHelper.Enable(_panel, _panel.DragHandle, SaveDashboardPosition);
			}

			var currentParent = _panel.Parent as Panel;
			if (currentParent != null && currentParent != canvas)
			{
				currentParent.Children.Remove(_panel);
			}

			if (!canvas.Children.Contains(_panel))
			{
				canvas.Children.Add(_panel);
			}
			OverlayInteractivityHelper.Register(_panel);

			return true;
		}

		private void HideByUser()
		{
			UserDismissed = true;
			Hide();
		}

		private async void HandleLocalSampleAction(
			LocalSampleActionKind action,
			int historyDays,
			int historyMatches)
		{
			if (_localSampleActionRunning)
			{
				return;
			}

			Func<LocalMetaDataActionResult> operation;
			var busyText = "正在重建本地样本，请稍候……";
			switch (action)
			{
				case LocalSampleActionKind.ApplyFilters:
					_config.LocalRecommendationHistoryDays = Math.Max(0, Math.Min(365, historyDays));
					_config.LocalRecommendationHistoryMatches = Math.Max(0, Math.Min(10000, historyMatches));
					_config.Save();
					operation = () => LocalMetaDataService.RebuildWithCurrentFilters(
						_config,
						MetaCompanionPlugin.DataDirectory,
						DateTime.Now);
					busyText = "正在应用最近天数和场数筛选……";
					break;
				case LocalSampleActionKind.Clear:
					var confirmation = MessageBox.Show(
						"只会清空插件当前用于推荐加权的本地对战数据；HDT 原始对战历史不会删除，之后可一键恢复。是否继续？",
						"Meta Companion",
						MessageBoxButton.YesNo,
						MessageBoxImage.Question,
						MessageBoxResult.No);
					if (confirmation != MessageBoxResult.Yes)
					{
						return;
					}
					operation = () => LocalMetaDataService.ClearLocalSamples(
						_config,
						MetaCompanionPlugin.DataDirectory,
						DateTime.Now);
					busyText = "正在清空插件本地对战数据……";
					break;
				case LocalSampleActionKind.RestoreCurrentPatch:
					operation = () => LocalMetaDataService.RestoreCurrentPatchHistory(
						_config,
						MetaCompanionPlugin.DataDirectory,
						DateTime.Now);
					busyText = "正在从 HDT 历史恢复当前补丁全部数据……";
					break;
				default:
					return;
			}

			await RunLocalSampleActionAsync(operation, busyText);
		}

		private async Task RunLocalSampleActionAsync(
			Func<LocalMetaDataActionResult> operation,
			string busyText)
		{
			if (_localSampleActionRunning || operation == null)
			{
				return;
			}

			_localSampleActionRunning = true;
			_localSampleActionStatus = busyText;
			UpdateLocalSamplePanelState();
			try
			{
				var result = await Task.Run(operation);
				_config.Save();
				_localSampleActionStatus = result == null
					? "本地样本处理完成。"
					: result.Message;
				RefreshDashboardSnapshotAfterLocalSampleAction();
			}
			catch (Exception ex)
			{
				_config.Save();
				Log.Warn("Dashboard local sample action failed: " + ex);
				_localSampleActionStatus = SettingsDiagnostics.BuildUserFacingFailure(
					"处理本地样本",
					"原始 HDT 历史未被删除，请稍后重试");
				MessageBox.Show(
					_localSampleActionStatus,
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}
			finally
			{
				_localSampleActionRunning = false;
				UpdateLocalSamplePanelState();
			}
		}

		private void RefreshDashboardSnapshotAfterLocalSampleAction()
		{
			if (_panel == null)
			{
				return;
			}
			try
			{
				_panel.Update(
					"卡组流派推荐",
					MetaDashboardSnapshot.Load(MetaCompanionPlugin.DataDirectory));
			}
			catch (Exception ex)
			{
				Log.Warn("Dashboard local sample reload failed: " + ex);
				_localSampleActionStatus += " 面板暂未重载，下次打开时会显示新结果。";
			}
		}

		private void UpdateLocalSamplePanelState()
		{
			_panel?.SetLocalSampleState(
				_config.LocalRecommendationHistoryDays,
				_config.LocalRecommendationHistoryMatches,
				_config.LocalRecommendationHistoryClearedAt > DateTime.MinValue,
				_localSampleActionStatus,
				_localSampleActionRunning);
		}

		private bool ApplyLastGameCorrection(string matchId, string correctedArchetype)
		{
			try
			{
				MatchHistoryRecorder.AppendCorrection(
					MetaCompanionPlugin.DataDirectory,
					matchId,
					correctedArchetype,
					"",
					"manual dashboard correction");
				Task.Run(() =>
					{
						try
						{
							var result = QuickDashboardRefresher.Refresh(
								_config,
								MetaCompanionPlugin.DataDirectory,
								DateTime.Now);
							RunOnOverlayThread(() =>
								{
									if (_panel != null && _panel.Visibility == Visibility.Visible)
									{
										_panel.Update(
											"\u5361\u7ec4\u6d41\u6d3e\u63a8\u8350",
											MetaDashboardSnapshot.Load(MetaCompanionPlugin.DataDirectory));
									}
									if (!result.EnvironmentUpdated)
									{
										MessageBox.Show(
											"\u4fee\u6b63\u5df2\u5199\u5165\uff1b\u5f53\u524d\u6570\u636e\u4e0d\u8db3\u4ee5\u7acb\u5373\u91cd\u751f\u6210\u672c\u5730\u73af\u5883\uff0c\u4e0b\u5c40\u540e\u4f1a\u518d\u5c1d\u8bd5\u5237\u65b0\u3002",
											"Meta Companion",
											MessageBoxButton.OK,
											MessageBoxImage.Information);
									}
								});
						}
						catch (Exception ex)
						{
							Log.Warn("Manual match correction refresh failed: " + ex);
							RunOnOverlayThread(() =>
								MessageBox.Show(
									"\u4fee\u6b63\u5df2\u5199\u5165\uff0c\u4f46\u7acb\u5373\u5237\u65b0\u672c\u5730\u73af\u5883\u5931\u8d25\uff1b\u4e0b\u5c40\u540e\u4f1a\u518d\u5c1d\u8bd5\u5237\u65b0\u3002",
									"Meta Companion",
									MessageBoxButton.OK,
									MessageBoxImage.Information));
						}
					});
				return true;
			}
			catch (Exception ex)
			{
				Log.Warn("Manual match correction write failed: " + ex);
				MessageBox.Show(
					SettingsDiagnostics.BuildUserFacingFailure(
						"保存修正",
						"请稍后重试；若仍失败，请打开插件日志查看原因"),
					"Meta Companion",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
				return false;
			}
		}

		private void PositionPanel()
		{
			var overlay = Core.OverlayWindow;
			if (overlay == null || _panel == null)
			{
				return;
			}

			if (OverlayDragHelper.HasCustomPosition(_panel))
			{
				return;
			}

			if (_config.HasDashboardPanelPosition)
			{
				if (IsSavedDashboardPositionUsable())
				{
					OverlayDragHelper.ApplyNormalizedPosition(
						_panel,
						_config.DashboardPanelPositionX,
						_config.DashboardPanelPositionY);
					return;
				}

				Log.Info("Resetting legacy dashboard panel position.");
				_config.HasDashboardPanelPosition = false;
				_config.DashboardPanelPositionX = 0.75;
				_config.DashboardPanelPositionY = 0.1;
				_config.Save();
			}

			Canvas.SetLeft(_panel, Double.NaN);
			Canvas.SetBottom(_panel, Double.NaN);
			Canvas.SetTop(_panel, overlay.Height * TopRatio);
			Canvas.SetRight(_panel, overlay.Width * RightRatio);
		}

		private void SaveDashboardPosition(FrameworkElement target)
		{
			var position = OverlayDragHelper.GetNormalizedPosition(target);
			_config.HasDashboardPanelPosition = true;
			_config.DashboardPanelPositionX = position.X;
			_config.DashboardPanelPositionY = position.Y;
			_config.Save();
		}

		private bool IsSavedDashboardPositionUsable()
		{
			return _config.DashboardPanelPositionX >= 0 &&
				_config.DashboardPanelPositionX <= 1 &&
				_config.DashboardPanelPositionY >= 0 &&
				_config.DashboardPanelPositionY <= MaxUsableSavedPositionY;
		}

		private void RestartTimer(TimeSpan duration)
		{
			if (duration <= TimeSpan.Zero)
			{
				_hideTimer?.Stop();
				return;
			}

			if (_hideTimer == null)
			{
				_hideTimer = new DispatcherTimer();
				_hideTimer.Tick += (sender, args) =>
				{
					_hideTimer.Stop();
					if (_panel != null)
					{
						_panel.Visibility = Visibility.Collapsed;
					}
				};
			}
			_hideTimer.Stop();
			_hideTimer.Interval = duration;
			_hideTimer.Start();
		}

		private static Canvas GetOverlayCanvas()
		{
			var overlayWindow = Core.OverlayWindow;
			if (overlayWindow == null)
			{
				return null;
			}

			var field = overlayWindow.GetType().GetField(
				"CanvasInfo",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			return field?.GetValue(overlayWindow) as Canvas;
		}

		private static void RunOnOverlayThread(Action action)
		{
			var overlayWindow = Core.OverlayWindow;
			if (overlayWindow == null)
			{
				action();
				return;
			}

			if (overlayWindow.Dispatcher.CheckAccess())
			{
				action();
			}
			else
			{
				overlayWindow.Dispatcher.BeginInvoke(action);
			}
		}
	}
}
