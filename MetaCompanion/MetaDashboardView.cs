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
			RunOnOverlayThread(() =>
				{
					if (!EnsurePanel())
					{
						return;
					}

					_panel.Update(title, MetaDashboardSnapshot.Load(MetaCompanionPlugin.DataDirectory));
					_panel.Visibility = Visibility.Visible;
					PositionPanel();
					RestartTimer(duration);
				});
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
				_panel = new MetaDashboardPanel(HideByUser, ApplyLastGameCorrection);
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
							Log.Warn("Manual match correction refresh failed: " + ex.Message);
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
				MessageBox.Show(
					"\u4fee\u6b63\u5199\u5165\u5931\u8d25: " + ex.Message,
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
