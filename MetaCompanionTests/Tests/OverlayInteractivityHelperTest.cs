using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class OverlayInteractivityHelperTest
	{
		[TestMethod]
		public void ShouldClickThrough_NoActiveTargets_AllowsGameClicks()
		{
			Assert.IsTrue(OverlayInteractivityHelper.ShouldClickThrough(0, 0, false));
			Assert.IsTrue(OverlayInteractivityHelper.ShouldClickThrough(0, 0, true));
		}

		[TestMethod]
		public void ShouldClickThrough_MouseOverActiveTarget_CapturesPanelClicks()
		{
			Assert.IsFalse(OverlayInteractivityHelper.ShouldClickThrough(1, 0, true));
		}

		[TestMethod]
		public void ShouldClickThrough_DuringDrag_HoldsOverlayInteractive()
		{
			Assert.IsFalse(OverlayInteractivityHelper.ShouldClickThrough(0, 1, false));
		}

		[TestMethod]
		public void ApplyClickThroughStyle_TogglesTransparentBitOnly()
		{
			const int transparent = 0x00000020;
			const int unrelatedStyle = 0x00080000;

			Assert.AreEqual(
				unrelatedStyle | transparent,
				OverlayInteractivityHelper.ApplyClickThroughStyle(unrelatedStyle, true));
			Assert.AreEqual(
				unrelatedStyle,
				OverlayInteractivityHelper.ApplyClickThroughStyle(unrelatedStyle | transparent, false));
		}
	}
}
