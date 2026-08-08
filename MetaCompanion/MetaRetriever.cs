using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace MetaCompanion
{
	class MetaRetriever : IMetaRetriever
	{
		private static readonly string MetaFilePath =
				Path.Combine(MetaCompanionPlugin.DataDirectory, @"metaDecks.xml");

		public Task<List<Deck>> RetrieveMetaDecks(PluginConfig config)
		{
			var deckCodeDecks = LoadDeckCodeDecks();
			if (deckCodeDecks.Count > 0)
			{
				Log.Info("已从牌组代码快照加载 " + deckCodeDecks.Count + " 套牌。");
				LogDeckClassCounts(deckCodeDecks);
				return Task.FromResult(deckCodeDecks);
			}

			if (!File.Exists(MetaFilePath))
			{
				Log.Warn("No deck code snapshot found; prediction data is empty until HSReplay deck codes are synced.");
				return Task.FromResult(new List<Deck>());
			}

			Log.Debug("Loading legacy MetaStats file");
			List<Deck> metaDecks = XmlManager<List<Deck>>.Load(MetaFilePath);
			Log.Info("已从旧版环境数据加载 " + metaDecks.Count + " 套牌。");
			LogDeckClassCounts(metaDecks);
			return Task.FromResult(metaDecks);
		}

		private static void LogDeckClassCounts(IEnumerable<Deck> decks)
		{
			var classCounts = decks
				.GroupBy(deck => NormalizeClass(deck.Class))
				.OrderBy(group => group.Key)
				.Select(group => LocalizeClassName(group.Key) + "=" + group.Count());
			Log.Info("牌组库职业分布：" + string.Join("，", classCounts));
		}

		private List<Deck> LoadDeckCodeDecks()
		{
			return LoadDeckCodeDecks(MetaCompanionPlugin.DataDirectory);
		}

		internal static List<Deck> LoadDeckCodeDecks(string dataDirectory)
		{
			var deckCodeFilePaths = BuildDeckCodeFilePaths(dataDirectory);
			var existingDeckCodeFilePaths = deckCodeFilePaths.Where(File.Exists).ToList();
			var existingFiles = SelectDeckCodeFilePaths(
				existingDeckCodeFilePaths,
				dataDirectory);
			if (existingFiles.Count == 0)
			{
				Log.Info("未找到牌组代码数据源。");
				return new List<Deck>();
			}
			Log.Info("正在读取 " + existingFiles.Count + " 个首选牌组代码数据源。");

			HearthDb.Cards.LoadBaseData();

			var decks = ImportDeckCodeFiles(existingFiles);
			if (decks.Count == 0 &&
				existingFiles.Any(path => IsHsReplayDeckCodeFile(path, dataDirectory)))
			{
				var fallbackFiles = SelectDeckCodeFallbackFilePaths(existingDeckCodeFilePaths, dataDirectory);
				if (fallbackFiles.Count > 0)
				{
					Log.Warn("No valid decks were imported from HSReplay deck-code snapshot; trying fallback sources: " +
						string.Join(", ", fallbackFiles));
					decks = ImportDeckCodeFiles(fallbackFiles);
				}
			}

			return decks;
		}

		private static List<Deck> ImportDeckCodeFiles(IEnumerable<string> filePaths)
		{
			var deckCodeFiles = filePaths.ToList();
			var decks = new List<Deck>();
			var unknownCardDbfIds = new Dictionary<int, int>();
			var deckCodeEntries = deckCodeFiles
				.SelectMany(File.ReadAllLines)
				.Select(ParseDeckCodeEntry)
				.Where(entry => entry != null)
				.GroupBy(entry => entry.Code)
				.Select(group => group.First())
				.ToList();

			foreach (var entry in deckCodeEntries)
			{
				try
				{
					decks.Add(ConvertDeckCode(entry.Code, entry.Name, unknownCardDbfIds));
				}
				catch (Exception ex)
				{
					Log.Warn("Ignoring invalid deck code: " + ex.Message);
				}
			}

			if (unknownCardDbfIds.Count > 0)
			{
				var unknownCardSummary = unknownCardDbfIds
					.OrderByDescending(pair => pair.Value)
					.ThenBy(pair => pair.Key)
					.Take(20)
					.Select(pair => pair.Key + "x" + pair.Value);
				Log.Warn("Skipped " + unknownCardDbfIds.Values.Sum() + " unknown card entries across " +
					unknownCardDbfIds.Count + " dbf ids while importing deck codes. Top ids: " +
					string.Join(", ", unknownCardSummary));
			}

			return decks;
		}

		private static bool IsHsReplayDeckCodeFile(string path, string dataDirectory)
		{
			return string.Equals(
				path,
				Path.Combine(dataDirectory, "hsreplay_deckcodes.txt"),
				StringComparison.OrdinalIgnoreCase);
		}

		private static List<string> SelectDeckCodeFallbackFilePaths(
			IEnumerable<string> existingFiles, string dataDirectory)
		{
			var existing = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
			var selected = new List<string>();
			var hsGuruDeckCodeFilePath = Path.Combine(dataDirectory, "hsguru_deckcodes.txt");
			var archetypeModelDeckCodeFilePath = Path.Combine(dataDirectory, "archetype_model_branches.tsv");
			var archetypeBranchDeckCodeFilePath = Path.Combine(dataDirectory, "archetype_deck_branches.tsv");
			if (existing.Contains(hsGuruDeckCodeFilePath))
			{
				selected.Add(hsGuruDeckCodeFilePath);
			}

			if (existing.Contains(archetypeModelDeckCodeFilePath))
			{
				selected.Add(archetypeModelDeckCodeFilePath);
			}

			// Legacy fallback for installations created before model and representative
			// branch snapshots were split.
			if (existing.Contains(archetypeBranchDeckCodeFilePath))
			{
				selected.Add(archetypeBranchDeckCodeFilePath);
			}
			return selected;
		}

		private static string[] BuildDeckCodeFilePaths(string dataDirectory)
		{
			return new[]
			{
				Path.Combine(dataDirectory, "deckcodes.txt"),
				Path.Combine(dataDirectory, "hsreplay_deckcodes.txt"),
				Path.Combine(dataDirectory, "hsguru_deckcodes.txt"),
				Path.Combine(dataDirectory, "archetype_model_branches.tsv"),
				Path.Combine(dataDirectory, "archetype_deck_branches.tsv")
			};
		}

		internal static List<string> SelectDeckCodeFilePaths(IEnumerable<string> existingFiles)
		{
			return SelectDeckCodeFilePaths(existingFiles, MetaCompanionPlugin.DataDirectory);
		}

		internal static List<string> SelectDeckCodeFilePaths(
			IEnumerable<string> existingFiles, string dataDirectory)
		{
			var existing = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
			var selected = new List<string>();
			var manualDeckCodeFilePath = Path.Combine(dataDirectory, "deckcodes.txt");
			var hsReplayDeckCodeFilePath = Path.Combine(dataDirectory, "hsreplay_deckcodes.txt");
			var hsGuruDeckCodeFilePath = Path.Combine(dataDirectory, "hsguru_deckcodes.txt");
			var archetypeModelDeckCodeFilePath = Path.Combine(dataDirectory, "archetype_model_branches.tsv");
			var archetypeBranchDeckCodeFilePath = Path.Combine(dataDirectory, "archetype_deck_branches.tsv");
			if (existing.Contains(manualDeckCodeFilePath))
			{
				selected.Add(manualDeckCodeFilePath);
			}

			if (existing.Contains(archetypeModelDeckCodeFilePath) &&
				IsCurrentPatchBranchSnapshot(archetypeModelDeckCodeFilePath, dataDirectory))
			{
				selected.Add(archetypeModelDeckCodeFilePath);
				return selected;
			}

			// Keep the old branch file as a migration fallback. New refreshes write
			// recognition data to archetype_model_branches.tsv and reserve this file
			// for same-scope representative deck copy buttons.
			if (existing.Contains(archetypeBranchDeckCodeFilePath) &&
				IsCurrentPatchBranchSnapshot(archetypeBranchDeckCodeFilePath, dataDirectory))
			{
				selected.Add(archetypeBranchDeckCodeFilePath);
				return selected;
			}

			if (existing.Contains(hsReplayDeckCodeFilePath))
			{
				selected.Add(hsReplayDeckCodeFilePath);
				return selected;
			}

			if (existing.Contains(hsGuruDeckCodeFilePath))
			{
				selected.Add(hsGuruDeckCodeFilePath);
			}

			if (existing.Contains(archetypeModelDeckCodeFilePath))
			{
				selected.Add(archetypeModelDeckCodeFilePath);
			}

			if (existing.Contains(archetypeBranchDeckCodeFilePath))
			{
				selected.Add(archetypeBranchDeckCodeFilePath);
			}
			return selected;
		}

		private static bool IsCurrentPatchBranchSnapshot(string path, string dataDirectory)
		{
			try
			{
				var candidateTimeRange = "";
				DateTimeOffset? candidateAsOf = null;
				var candidatePatchVersion = "";
				foreach (var rawLine in File.ReadLines(path).Take(32))
				{
					var line = rawLine.Trim();
					if (line.StartsWith("# CandidateTimeRange:", StringComparison.OrdinalIgnoreCase))
					{
						candidateTimeRange = line.Substring("# CandidateTimeRange:".Length).Trim();
					}
					else if (line.StartsWith("# CandidateAsOf:", StringComparison.OrdinalIgnoreCase))
					{
						candidateAsOf = ParseDateTimeOffset(line.Substring("# CandidateAsOf:".Length).Trim());
					}
					else if (line.StartsWith("# PatchVersion:", StringComparison.OrdinalIgnoreCase))
					{
						candidatePatchVersion = line.Substring("# PatchVersion:".Length).Trim();
					}
					else if (!line.StartsWith("#", StringComparison.Ordinal) && line.Length > 0)
					{
						break;
					}
				}

				if (!IsSupportedBranchTimeRange(candidateTimeRange))
				{
					return false;
				}
				if (string.Equals(
					candidateTimeRange,
					"CURRENT_PATCH",
					StringComparison.OrdinalIgnoreCase))
				{
					var candidatePublicPatch = NormalizePublicPatchVersion(candidatePatchVersion);
					var localPublicPatch = ReadLocalPublicPatchVersion(dataDirectory);
					return !string.IsNullOrWhiteSpace(candidatePublicPatch) &&
						string.Equals(
							candidatePublicPatch,
							localPublicPatch,
							StringComparison.OrdinalIgnoreCase);
				}

				var patchTime = ReadPatchMarkerTime(dataDirectory);
				if (!patchTime.HasValue)
				{
					return true;
				}

				if (candidateAsOf.HasValue)
				{
					return candidateAsOf.Value >= patchTime.Value;
				}

				return new DateTimeOffset(File.GetLastWriteTime(path)) >= patchTime.Value;
			}
			catch (Exception ex)
			{
				Log.Warn("Ignoring current-patch branch snapshot preference: " + ex.Message);
				return false;
			}
		}

		private static bool IsSupportedBranchTimeRange(string value)
		{
			return string.Equals(value, "CURRENT_PATCH", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(value, "LAST_1_DAY", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(value, "LAST_3_DAYS", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(value, "LAST_7_DAYS", StringComparison.OrdinalIgnoreCase);
		}

		private static DateTimeOffset? ReadPatchMarkerTime(string dataDirectory)
		{
			var markerPath = Path.Combine(dataDirectory, "patch_marker.txt");
			if (!File.Exists(markerPath))
			{
				return null;
			}
			return ParseDateTimeOffset(File.ReadAllText(markerPath).Trim());
		}

		private static string ReadLocalPublicPatchVersion(string dataDirectory)
		{
			if (string.IsNullOrWhiteSpace(dataDirectory))
			{
				return "";
			}
			var path = Path.Combine(dataDirectory, "patch_version.txt");
			try
			{
				return File.Exists(path)
					? NormalizePublicPatchVersion(File.ReadAllText(path, Encoding.UTF8))
					: "";
			}
			catch
			{
				return "";
			}
		}

		private static string NormalizePublicPatchVersion(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return "";
			}
			var match = Regex.Match(
				value,
				@"\b(?<version>\d+\.\d+\.\d+)(?:\.\d+)?\b",
				RegexOptions.CultureInvariant);
			return match.Success ? match.Groups["version"].Value : "";
		}

		private static DateTimeOffset? ParseDateTimeOffset(string value)
		{
			DateTimeOffset parsed;
			if (DateTimeOffset.TryParse(value, out parsed))
			{
				return parsed;
			}
			return null;
		}

		internal static DeckCodeEntry ParseDeckCodeEntry(string line)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				return null;
			}

			line = line.Trim();
			if (line.StartsWith("#"))
			{
				return null;
			}

			var match = Regex.Match(line, @"AAE[A-Za-z0-9+/=]{20,}");
			if (!match.Success)
			{
				return null;
			}

			var name = line.Substring(0, match.Index).Trim().TrimEnd('|', '-').Trim();
			return new DeckCodeEntry
			{
				Code = match.Value,
				Name = string.IsNullOrEmpty(name) ? "Imported Meta Deck" : name
			};
		}

		internal static Deck ConvertDeckCode(string deckCode, string deckName,
			IDictionary<int, int> unknownCardDbfIds)
		{
			var deckString = HearthDb.Deckstrings.DeckSerializer.Deserialize(deckCode);
			var cards = new List<Card>();
			foreach (var entry in deckString.CardDbfIds)
			{
				var hearthDbCard = HearthDb.Cards.GetFromDbfId(entry.Key, false);
				if (hearthDbCard == null)
				{
					unknownCardDbfIds[entry.Key] =
						(unknownCardDbfIds.ContainsKey(entry.Key) ? unknownCardDbfIds[entry.Key] : 0) + entry.Value;
					continue;
				}

				var card = new Card(hearthDbCard, false);
				card.Count = entry.Value;
				cards.Add(card);
			}

			if (cards.Count == 0)
			{
				throw new InvalidDataException("No known cards in deck code for hero dbf id " + deckString.HeroDbfId);
			}

			var deckClass = InferClass(deckString.HeroDbfId, cards);
			if (string.IsNullOrEmpty(deckClass))
			{
				throw new InvalidDataException("Unable to infer deck class for hero dbf id " + deckString.HeroDbfId);
			}

			var deck = new Deck
			{
				Name = deckName,
				Class = deckClass,
				DeckId = Guid.NewGuid()
			};

			foreach (var card in cards)
			{
				deck.Cards.Add(card);
			}

			return deck;
		}

		internal static string InferClass(int heroDbfId, IEnumerable<Card> cards)
		{
			var hero = HearthDb.Cards.GetFromDbfId(heroDbfId, false);
			var heroClass = NormalizeClass(hero == null ? null : hero.Class.ToString());
			if (!string.IsNullOrEmpty(heroClass) && heroClass != "Neutral")
			{
				return heroClass;
			}

			var knownHeroClass = NormalizeClass(GetKnownHeroClass(heroDbfId));
			if (!string.IsNullOrEmpty(knownHeroClass))
			{
				return knownHeroClass;
			}

			return cards
				.Select(card => NormalizeClass(card.PlayerClass))
				.Where(playerClass => !string.IsNullOrEmpty(playerClass) && playerClass != "Neutral")
				.GroupBy(playerClass => playerClass)
				.OrderByDescending(group => group.Count())
				.Select(group => group.Key)
				.FirstOrDefault();
		}

		private static string GetKnownHeroClass(int heroDbfId)
		{
			switch (heroDbfId)
			{
				case 637:
					return "Mage";
				case 274:
					return "Druid";
				case 31:
					return "Hunter";
				case 1066:
					return "Shaman";
				case 813:
					return "Priest";
				case 930:
					return "Rogue";
				case 893:
					return "Warlock";
				case 671:
					return "Paladin";
				case 7:
					return "Warrior";
				case 56550:
					return "Demon Hunter";
				case 78065:
					return "Death Knight";
				default:
					return null;
			}
		}

		public static string NormalizeClass(string playerClass)
		{
			var classKey = (playerClass ?? "")
				.Replace(" ", "")
				.Replace("_", "")
				.Replace("-", "")
				.ToUpperInvariant();
			switch (classKey)
			{
				case "NEUTRAL":
					return "Neutral";
				case "DEATHKNIGHT":
					return "Death Knight";
				case "DEMONHUNTER":
					return "Demon Hunter";
				case "DRUID":
					return "Druid";
				case "HUNTER":
					return "Hunter";
				case "MAGE":
					return "Mage";
				case "PALADIN":
					return "Paladin";
				case "PRIEST":
					return "Priest";
				case "ROGUE":
					return "Rogue";
				case "SHAMAN":
					return "Shaman";
				case "WARLOCK":
					return "Warlock";
				case "WARRIOR":
					return "Warrior";
				default:
					return playerClass;
			}
		}

		private static string LocalizeClassName(string playerClass)
		{
			switch (NormalizeClass(playerClass))
			{
				case "Death Knight":
					return "死亡骑士";
				case "Demon Hunter":
					return "恶魔猎手";
				case "Druid":
					return "德鲁伊";
				case "Hunter":
					return "猎人";
				case "Mage":
					return "法师";
				case "Paladin":
					return "圣骑士";
				case "Priest":
					return "牧师";
				case "Rogue":
					return "潜行者";
				case "Shaman":
					return "萨满祭司";
				case "Warlock":
					return "术士";
				case "Warrior":
					return "战士";
				case "Neutral":
					return "中立";
				default:
					return "未知职业";
			}
		}

		internal class DeckCodeEntry
		{
			public string Code { get; set; }
			public string Name { get; set; }
		}
	}
}
