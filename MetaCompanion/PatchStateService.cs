using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MetaCompanion
{
	internal class HearthstonePatchInfo
	{
		public string Version { get; set; } = "";
		public DateTime? PatchTime { get; set; }
		public string Source { get; set; } = "";
	}

	internal class PatchStateResult
	{
		public bool PatchChanged { get; set; }
		public string PatchVersion { get; set; } = "";
		public string PatchEpoch { get; set; } = "";
		public DateTime? PatchTime { get; set; }
		public int ArchivedFileCount { get; set; }
		public string ArchiveDirectory { get; set; } = "";
	}

	internal static class PatchStateService
	{
		private static readonly Regex PatchVersionRegex =
			new Regex(@"\b(\d+\.\d+\.\d+(?:\.\d+)?)\b", RegexOptions.Compiled);

		private static readonly string[] ActiveLocalFiles =
		{
			"match_history.tsv",
			"match_corrections.tsv",
			"prediction_timeline.tsv",
			"hdt_opponent_history.tsv",
			"local_meta_archetypes.tsv",
			"local_meta_environment.tsv",
			"local_meta_summary.json",
			"post_game_data_refresh.last",
			Path.Combine("Premium", "Meta", "latest", "personal_recommendations.tsv"),
			Path.Combine("Premium", "Meta", "latest", "personal_recommendations.json")
		};

		internal static PatchStateResult EnsureCurrentPatchState(string dataDirectory)
		{
			return EnsureCurrentPatchState(dataDirectory, DetectHearthstonePatch(), DateTime.Now);
		}

		internal static PatchStateResult EnsureCurrentPatchState(
			string dataDirectory,
			HearthstonePatchInfo detectedPatch,
			DateTime now)
		{
			var result = new PatchStateResult();
			if (string.IsNullOrWhiteSpace(dataDirectory))
			{
				return result;
			}

			Directory.CreateDirectory(dataDirectory);
			detectedPatch = detectedPatch ?? new HearthstonePatchInfo();
			var patchVersion = NormalizePatchVersion(detectedPatch.Version);
			var detectedPatchTime = detectedPatch.PatchTime;
			var versionPath = GetPatchVersionPath(dataDirectory);
			var markerPath = GetPatchMarkerPath(dataDirectory);
			var storedVersion = NormalizePatchVersion(ReadTextIfExists(versionPath));
			var storedMarker = ParseDate(ReadTextIfExists(markerPath));
			var versionChanged = HasVersionChanged(storedVersion, patchVersion);
			var patchTime = versionChanged &&
				(!detectedPatchTime.HasValue ||
					(storedMarker.HasValue && detectedPatchTime.Value <= storedMarker.Value))
				? (DateTime?)now
				: LaterOf(storedMarker, detectedPatchTime);

			var patchChanged = IsPatchBoundary(
				storedVersion, storedMarker, patchVersion, detectedPatchTime);
			if (patchChanged)
			{
				result.ArchiveDirectory = ArchiveActiveLocalFiles(
					dataDirectory,
					patchVersion,
					now,
					out var archivedCount);
				result.ArchivedFileCount = archivedCount;
			}

			if (!string.IsNullOrWhiteSpace(patchVersion))
			{
				File.WriteAllText(versionPath, patchVersion + Environment.NewLine, Encoding.UTF8);
			}

			if (patchTime.HasValue &&
				(patchChanged || !storedMarker.HasValue || storedMarker.Value < patchTime.Value.AddMinutes(-1)))
			{
				File.WriteAllText(
					markerPath,
					patchTime.Value.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine,
					Encoding.UTF8);
			}

			var patchEpoch = BuildPatchEpoch(patchVersion, patchTime);
			if (!string.IsNullOrWhiteSpace(patchEpoch))
			{
				File.WriteAllText(
					GetPatchEpochPath(dataDirectory),
					patchEpoch + Environment.NewLine,
					Encoding.UTF8);
			}

			result.PatchChanged = patchChanged;
			result.PatchVersion = patchVersion;
			result.PatchEpoch = patchEpoch;
			result.PatchTime = patchTime;
			return result;
		}

		internal static HearthstonePatchInfo DetectHearthstonePatch()
		{
			var exePath = ResolveHearthstoneExePath();
			if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
			{
				return new HearthstonePatchInfo();
			}

			var version = "";
			DateTime? patchTime = null;
			var source = exePath;
			var productDbPath = Path.Combine(Path.GetDirectoryName(exePath), ".product.db");
			if (File.Exists(productDbPath))
			{
				version = NormalizePatchVersion(
					Encoding.ASCII.GetString(File.ReadAllBytes(productDbPath)));
				if (!string.IsNullOrWhiteSpace(version))
				{
					patchTime = File.GetLastWriteTime(productDbPath);
					source = productDbPath;
				}
			}

			if (string.IsNullOrWhiteSpace(version))
			{
				version = NormalizePatchVersion(FileVersionInfo.GetVersionInfo(exePath).ProductVersion);
				patchTime = File.GetLastWriteTime(exePath);
			}

			return new HearthstonePatchInfo
			{
				Version = version,
				PatchTime = patchTime,
				Source = source
			};
		}

		private static bool IsPatchBoundary(
			string storedVersion,
			DateTime? storedMarker,
			string detectedVersion,
			DateTime? detectedPatchTime)
		{
			if (string.IsNullOrWhiteSpace(detectedVersion) && !detectedPatchTime.HasValue)
			{
				return false;
			}

			if (HasVersionChanged(storedVersion, detectedVersion))
			{
				return true;
			}

			return detectedPatchTime.HasValue &&
				storedMarker.HasValue &&
				storedMarker.Value < detectedPatchTime.Value.AddMinutes(-1);
		}

		private static bool HasVersionChanged(string storedVersion, string detectedVersion)
		{
			return !string.IsNullOrWhiteSpace(storedVersion) &&
				!string.IsNullOrWhiteSpace(detectedVersion) &&
				!string.Equals(storedVersion, detectedVersion, StringComparison.OrdinalIgnoreCase);
		}

		private static string ArchiveActiveLocalFiles(
			string dataDirectory,
			string patchVersion,
			DateTime now,
			out int archivedCount)
		{
			archivedCount = 0;
			var archiveDirectory = Path.Combine(
				dataDirectory,
				"PatchArchives",
				now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
				(string.IsNullOrWhiteSpace(patchVersion) ? "" : "-" + SanitizeFileName(patchVersion)));

			foreach (var relativePath in ActiveLocalFiles)
			{
				var source = Path.Combine(dataDirectory, relativePath);
				if (!File.Exists(source))
				{
					continue;
				}

				var destination = Path.Combine(archiveDirectory, relativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(destination));
				File.Move(source, GetUniquePath(destination));
				archivedCount++;
			}

			return archivedCount > 0 ? archiveDirectory : "";
		}

		private static string ResolveHearthstoneExePath()
		{
			try
			{
				var process = Process.GetProcessesByName("Hearthstone")
					.FirstOrDefault(item =>
					{
						try
						{
							return !string.IsNullOrWhiteSpace(item.MainModule?.FileName) &&
								File.Exists(item.MainModule.FileName);
						}
						catch
						{
							return false;
						}
					});
				if (process != null)
				{
					return process.MainModule.FileName;
				}
			}
			catch
			{
			}

			foreach (var candidate in new[]
			{
				@"F:\Hearthstone\Hearthstone.exe",
				@"C:\Program Files (x86)\Hearthstone\Hearthstone.exe",
				@"C:\Program Files\Hearthstone\Hearthstone.exe"
			})
			{
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}
			return "";
		}

		private static string NormalizePatchVersion(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "";
			}

			var match = PatchVersionRegex.Match(value);
			return match.Success ? match.Groups[1].Value : "";
		}

		private static DateTime? ParseDate(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}

			DateTime parsed;
			if (DateTime.TryParse(
				value.Trim(),
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeLocal,
				out parsed))
			{
				return parsed;
			}
			return DateTime.TryParse(value.Trim(), out parsed) ? (DateTime?)parsed : null;
		}

		private static string ReadTextIfExists(string path)
		{
			return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
		}

		private static string GetPatchMarkerPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "patch_marker.txt");
		}

		private static string GetPatchVersionPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "patch_version.txt");
		}

		private static string GetPatchEpochPath(string dataDirectory)
		{
			return Path.Combine(dataDirectory, "patch_epoch.txt");
		}

		private static DateTime? LaterOf(DateTime? first, DateTime? second)
		{
			if (!first.HasValue)
			{
				return second;
			}
			if (!second.HasValue)
			{
				return first;
			}
			return first.Value >= second.Value ? first : second;
		}

		private static string BuildPatchEpoch(string patchVersion, DateTime? patchTime)
		{
			if (string.IsNullOrWhiteSpace(patchVersion) && !patchTime.HasValue)
			{
				return "";
			}
			return (string.IsNullOrWhiteSpace(patchVersion) ? "unknown" : patchVersion) + "@" +
				(patchTime.HasValue
					? patchTime.Value.ToString("o", CultureInfo.InvariantCulture)
					: "unknown");
		}

		private static string GetUniquePath(string path)
		{
			if (!File.Exists(path))
			{
				return path;
			}

			var directory = Path.GetDirectoryName(path);
			var name = Path.GetFileNameWithoutExtension(path);
			var extension = Path.GetExtension(path);
			for (var index = 1; ; index++)
			{
				var candidate = Path.Combine(
					directory,
					name + "." + index.ToString(CultureInfo.InvariantCulture) + extension);
				if (!File.Exists(candidate))
				{
					return candidate;
				}
			}
		}

		private static string SanitizeFileName(string value)
		{
			var invalid = Path.GetInvalidFileNameChars();
			return new string((value ?? "")
				.Select(ch => invalid.Contains(ch) ? '_' : ch)
				.ToArray());
		}
	}
}
