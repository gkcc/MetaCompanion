using Hearthstone_Deck_Tracker.API;
using System;
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

		private readonly PluginConfig _config;
		private MetaDashboardPanel _panel;
		private DispatcherTimer _hideTimer;

		public MetaDashboardView(PluginConfig config)
		{
			_config = config ?? new PluginConfig();
		}

		public void ShowStandardStart()
		{
			Show("标准对战环境", TimeSpan.FromSeconds(11), false);
		}

		public void ShowPostGame()
		{
			var duration = _config.PostGameDashboardPersistent
				? TimeSpan.Zero
				: TimeSpan.FromSeconds(Math.Max(0, _config.PostGameDashboardAutoHideSeconds));
			Show("赛后环境速览", duration, true);
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

		private void Show(string title, TimeSpan duration, bool persistent)
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
					if (persistent && _config.PostGameDashboardPersistent)
					{
						_hideTimer?.Stop();
					}
					else
					{
						RestartTimer(duration);
					}
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
				_panel = new MetaDashboardPanel(Hide);
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
				OverlayDragHelper.ApplyNormalizedPosition(
					_panel,
					_config.DashboardPanelPositionX,
					_config.DashboardPanelPositionY);
				return;
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
