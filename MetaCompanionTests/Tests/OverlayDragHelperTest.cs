using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class OverlayDragHelperTest
	{
		[TestMethod]
		public void CalculateDragPosition_ClampsWithinCanvas()
		{
			var position = OverlayDragHelper.CalculateDragPosition(
				new Point(50, 60),
				new Point(100, 100),
				new Point(500, 500),
				new Size(100, 80),
				new Size(300, 240));

			Assert.AreEqual(200, position.X);
			Assert.AreEqual(160, position.Y);
		}

		[TestMethod]
		public void NormalizeAndDenormalizePosition_RoundTrips()
		{
			var position = new Point(100, 80);
			var targetSize = new Size(100, 80);
			var canvasSize = new Size(300, 240);

			var normalized = OverlayDragHelper.NormalizePosition(position, targetSize, canvasSize);
			var denormalized = OverlayDragHelper.DenormalizePosition(
				normalized, targetSize, canvasSize);

			Assert.AreEqual(position.X, denormalized.X);
			Assert.AreEqual(position.Y, denormalized.Y);
		}

		[TestMethod]
		public void PreviewDrag_StartMoveEnd_SavesPosition()
		{
			var canvas = CreateCanvasWithTarget(out var target, out var handle, out var child);
			var savedCount = 0;
			Point savedPosition = new Point(-1, -1);
			OverlayDragHelper.Enable(target, handle, element =>
				{
					savedCount++;
					savedPosition = OverlayDragHelper.GetNormalizedPosition(element);
				});

			var down = RaisePreviewDown(handle, handle);
			var move = RaisePreviewMove(handle, handle);
			var up = RaisePreviewUp(handle, handle);

			Assert.IsTrue(down.Handled);
			Assert.IsTrue(move.Handled);
			Assert.IsTrue(up.Handled);
			Assert.AreEqual(1, savedCount);
			Assert.IsTrue(OverlayDragHelper.HasCustomPosition(target));
			Assert.AreEqual(0.25, savedPosition.X, 0.001);
			Assert.AreEqual(0.375, savedPosition.Y, 0.001);
			Assert.AreSame(canvas, target.Parent);
		}

		[TestMethod]
		public void PreviewDrag_StartsWhenChildHandlesNormalMouseDown()
		{
			CreateCanvasWithTarget(out var target, out var handle, out var child);
			var savedCount = 0;
			var childNormalMouseDownCount = 0;
			child.MouseLeftButtonDown += (sender, args) =>
				{
					childNormalMouseDownCount++;
					args.Handled = true;
				};
			OverlayDragHelper.Enable(target, handle, element => savedCount++);

			var previewDown = RaisePreviewDown(handle, child);
			var normalDown = RaiseNormalDown(child);
			var previewUp = RaisePreviewUp(handle, child);

			Assert.IsTrue(previewDown.Handled);
			Assert.IsTrue(normalDown.Handled);
			Assert.IsTrue(previewUp.Handled);
			Assert.AreEqual(1, childNormalMouseDownCount);
			Assert.AreEqual(1, savedCount);
		}

		private static Canvas CreateCanvasWithTarget(
			out Border target,
			out Grid handle,
			out TextBlock child)
		{
			var canvas = new Canvas
			{
				Width = 300,
				Height = 240
			};
			target = new Border
			{
				Width = 100,
				Height = 80
			};
			handle = new Grid
			{
				Width = 100,
				Height = 20
			};
			child = new TextBlock { Text = "drag" };
			handle.Children.Add(child);
			target.Child = handle;
			canvas.Children.Add(target);
			Canvas.SetLeft(target, 50);
			Canvas.SetTop(target, 60);
			canvas.Measure(new Size(300, 240));
			canvas.Arrange(new Rect(0, 0, 300, 240));
			canvas.UpdateLayout();
			return canvas;
		}

		private static MouseButtonEventArgs RaisePreviewDown(UIElement routeSource, object originalSource)
		{
			var args = NewMouseButtonArgs(UIElement.PreviewMouseLeftButtonDownEvent, originalSource);
			routeSource.RaiseEvent(args);
			return args;
		}

		private static MouseEventArgs RaisePreviewMove(UIElement routeSource, object originalSource)
		{
			var args = new MouseEventArgs(Mouse.PrimaryDevice, System.Environment.TickCount)
			{
				RoutedEvent = UIElement.PreviewMouseMoveEvent,
				Source = originalSource
			};
			routeSource.RaiseEvent(args);
			return args;
		}

		private static MouseButtonEventArgs RaisePreviewUp(UIElement routeSource, object originalSource)
		{
			var args = NewMouseButtonArgs(UIElement.PreviewMouseLeftButtonUpEvent, originalSource);
			routeSource.RaiseEvent(args);
			return args;
		}

		private static MouseButtonEventArgs RaiseNormalDown(UIElement routeSource)
		{
			var args = NewMouseButtonArgs(UIElement.MouseLeftButtonDownEvent, routeSource);
			routeSource.RaiseEvent(args);
			return args;
		}

		private static MouseButtonEventArgs NewMouseButtonArgs(RoutedEvent routedEvent, object source)
		{
			return new MouseButtonEventArgs(
				Mouse.PrimaryDevice,
				System.Environment.TickCount,
				MouseButton.Left)
			{
				RoutedEvent = routedEvent,
				Source = source
			};
		}
	}
}
