using Hearthstone_Deck_Tracker;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace MetaCompanion
{
	public class HdtReplayInfo
	{
		public static readonly HdtReplayInfo Empty = new HdtReplayInfo();

		public string ReplayFile { get; set; } = "";
		public string ReplayPath { get; set; } = "";
		public string UploadId { get; set; } = "";
		public string ReplayUrl { get; set; } = "";

		public static HdtReplayInfo FindLatestReplay(DateTime startedAt, string opponentClass)
		{
			var deckStatsPath = Path.Combine(Config.AppDataPath, "DeckStats.xml");
			if (!File.Exists(deckStatsPath))
			{
				return Empty;
			}

			try
			{
				var doc = new XmlDocument();
				doc.Load(deckStatsPath);
				var games = doc.SelectNodes("//Game");
				if (games == null)
				{
					return Empty;
				}

				var lowerBound = startedAt.AddMinutes(-5);
				var best = games
					.Cast<XmlNode>()
					.Select(game => new
					{
						Node = game,
						StartTime = TryParseDate(GetNodeText(game, "StartTime"))
					})
					.Where(game => game.StartTime.HasValue && game.StartTime.Value >= lowerBound)
					.OrderByDescending(game => game.StartTime.Value)
					.FirstOrDefault();
				return best == null ? Empty : FromGameNode(best.Node);
			}
			catch (Exception ex)
			{
				Log.Warn("Unable to read HDT replay info: " + ex.Message);
				return Empty;
			}
		}

		private static HdtReplayInfo FromGameNode(XmlNode game)
		{
			var replayFile = GetNodeText(game, "ReplayFile");
			var uploadId = GetNodeText(game.SelectSingleNode("HsReplay"), "UploadId");
			var replayUrl = GetNodeText(game.SelectSingleNode("HsReplay"), "ReplayUrl");
			return new HdtReplayInfo
			{
				ReplayFile = replayFile,
				ReplayPath = ResolveReplayPath(replayFile),
				UploadId = uploadId,
				ReplayUrl = replayUrl
			};
		}

		private static string ResolveReplayPath(string replayFile)
		{
			if (string.IsNullOrWhiteSpace(replayFile))
			{
				return "";
			}

			var path = Path.Combine(Config.AppDataPath, "Replays", replayFile);
			return File.Exists(path) ? path : "";
		}

		private static DateTime? TryParseDate(string value)
		{
			DateTime result;
			if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out result) ||
				DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
			{
				return result;
			}
			return null;
		}

		private static string GetNodeText(XmlNode node, string name)
		{
			return GetNodeText(node?.SelectSingleNode(name));
		}

		private static string GetNodeText(XmlNode node)
		{
			return node == null ? "" : node.InnerText ?? "";
		}
	}
}
