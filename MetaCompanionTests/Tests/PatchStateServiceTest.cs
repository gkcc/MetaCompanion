using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class PatchStateServiceTest
	{
		private string _tempDirectory;

		[TestInitialize]
		public void Initialize()
		{
			_tempDirectory = System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				"MetaCompanionPatchStateTests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_tempDirectory);
		}

		[TestCleanup]
		public void Cleanup()
		{
			if (Directory.Exists(_tempDirectory))
			{
				Directory.Delete(_tempDirectory, true);
			}
		}

		[TestMethod]
		public void EnsureCurrentPatchState_ArchivesActiveLocalFilesWhenPatchChanges()
		{
			Write("patch_version.txt", "35.6.2");
			Write("patch_marker.txt", "2026-06-12T01:00:00+08:00");
			Write("match_history.tsv", "old match");
			Write("prediction_timeline.tsv", "old timeline");
			Write("local_meta_summary.json", "{}");
			Write(System.IO.Path.Combine("Premium", "Meta", "latest", "personal_recommendations.tsv"), "old recs");
			Write(System.IO.Path.Combine("Premium", "Meta", "latest", "summary.json"), "remote cache");

			var result = PatchStateService.EnsureCurrentPatchState(
				_tempDirectory,
				new HearthstonePatchInfo
				{
					Version = "36.0.0.12345",
					PatchTime = new DateTime(2026, 7, 7, 19, 16, 0)
				},
				new DateTime(2026, 7, 8, 12, 0, 0));

			Assert.IsTrue(result.PatchChanged);
			Assert.AreEqual("36.0.0", result.PatchVersion);
			Assert.AreEqual(4, result.ArchivedFileCount);
			Assert.IsFalse(File.Exists(ResolvePath("match_history.tsv")));
			Assert.IsFalse(File.Exists(ResolvePath("prediction_timeline.tsv")));
			Assert.IsFalse(File.Exists(ResolvePath("local_meta_summary.json")));
			Assert.IsFalse(File.Exists(ResolvePath(System.IO.Path.Combine("Premium", "Meta", "latest", "personal_recommendations.tsv"))));
			Assert.IsTrue(File.Exists(ResolvePath(System.IO.Path.Combine("Premium", "Meta", "latest", "summary.json"))));
			Assert.AreEqual("36.0.0", File.ReadAllText(ResolvePath("patch_version.txt"), Encoding.UTF8).Trim());
			StringAssert.Contains(
				File.ReadAllText(ResolvePath("patch_marker.txt"), Encoding.UTF8),
				"2026-07-07T19:16:00");
			Assert.IsTrue(File.Exists(Path.Combine(result.ArchiveDirectory, "match_history.tsv")));
		}

		[TestMethod]
		public void EnsureCurrentPatchState_DoesNotArchiveWhenStateIsCurrent()
		{
			Write("patch_version.txt", "36.0.0");
			Write("patch_marker.txt", "2026-07-07T19:16:00");
			Write("match_history.tsv", "current match");

			var result = PatchStateService.EnsureCurrentPatchState(
				_tempDirectory,
				new HearthstonePatchInfo
				{
					Version = "36.0.0",
					PatchTime = new DateTime(2026, 7, 7, 19, 16, 0)
				},
				new DateTime(2026, 7, 8, 12, 0, 0));

			Assert.IsFalse(result.PatchChanged);
			Assert.AreEqual(0, result.ArchivedFileCount);
			Assert.IsTrue(File.Exists(ResolvePath("match_history.tsv")));
		}

		private void Write(string relativePath, string value)
		{
			var path = ResolvePath(relativePath);
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
			File.WriteAllText(path, value, Encoding.UTF8);
		}

		private string ResolvePath(string relativePath)
		{
			return System.IO.Path.Combine(_tempDirectory, relativePath);
		}
	}
}
