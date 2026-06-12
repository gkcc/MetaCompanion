using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace MetaCompanion
{
	class MetaRetriever
	{
		// How many days we wait before updating the meta since the last download.
		private const double RecentDownloadTimeoutDays = 1;
		private const string MetaVersionUrl = "http://metastats.net/metadetector/metaversion.php";
		private const string MetaFileUrl = "https://s3.amazonaws.com/metadetector/metaDecks.xml.gz";
		private static readonly string MetaFilePath =
				Path.Combine(MetaCompanionPlugin.DataDirectory, @"metaDecks.xml");
		private static readonly string MetaArchivePath = MetaFilePath + ".gz";
		private static readonly string ManualDeckCodeFilePath =
				Path.Combine(MetaCompanionPlugin.DataDirectory, @"deckcodes.txt");
		private static readonly string ArchetypeBranchDeckCodeFilePath =
				Path.Combine(MetaCompanionPlugin.DataDirectory, @"archetype_deck_branches.tsv");
		private static readonly string HsReplayDeckCodeFilePath =
				Path.Combine(MetaCompanionPlugin.DataDirectory, @"hsreplay_deckcodes.txt");
		private static readonly string HsGuruDeckCodeFilePath =
				Path.Combine(MetaCompanionPlugin.DataDirectory, @"hsguru_deckcodes.txt");
		private static readonly string[] DeckCodeFilePaths =
		{
				ManualDeckCodeFilePath,
				HsReplayDeckCodeFilePath,
				HsGuruDeckCodeFilePath,
				ArchetypeBranchDeckCodeFilePath
		};

		public async Task<List<Deck>> RetrieveMetaDecks(PluginConfig config)
		{
			var deckCodeDecks = LoadDeckCodeDecks();
			if (deckCodeDecks.Count > 0)
			{
				Log.Info("Meta retrieved from deck code file, " + deckCodeDecks.Count + " decks loaded.");
				LogDeckClassCounts(deckCodeDecks);
				return deckCodeDecks;
			}

			// First check if we need to download the meta file.
			string newMetaVersion = "";
			if (!File.Exists(MetaFilePath))
			{
				Log.Info("No meta file found.");
				newMetaVersion = await RetrieveLegacyMetaVersion();
			}
			else
			{
				double daysSinceLastDownload = (DateTime.Now - config.CurrentMetaFileDownloadTime).TotalDays;
				if (daysSinceLastDownload > RecentDownloadTimeoutDays)
				{
					Log.Info(daysSinceLastDownload +
							" days since meta file has been updated, checking for new version.");
					newMetaVersion = await RetrieveLegacyMetaVersion();
					if (newMetaVersion.Trim() != "" && newMetaVersion != config.CurrentMetaFileVersion)
					{
						Log.Info("New version detected: " + newMetaVersion +
								", old version: " + config.CurrentMetaFileVersion);
					}
					else
					{
						Log.Debug("Newest version of meta file matches cached version: " + newMetaVersion);
						newMetaVersion = "";
					}
				}
				else
				{
					Log.Debug("Cached meta file is only " + daysSinceLastDownload + " days old.");
				}
			}

			if (newMetaVersion == "" && !File.Exists(MetaFilePath))
			{
				Log.Warn("No deck code file and no usable legacy MetaStats file; prediction data is empty.");
				return new List<Deck>();
			}

			if (newMetaVersion != "")
			{
				Log.Info("Downloading new meta file.");
				using (WebClient client = new WebClient())
				{
					await client.DownloadFileTaskAsync(MetaFileUrl, MetaArchivePath);
				}

				Log.Info("Meta file downloaded, unzipping...");
				FileInfo archiveFile = new FileInfo(MetaArchivePath);

				using (FileStream archiveFileStream = archiveFile.OpenRead())
				{
					using (FileStream unzippedFileStream = File.Create(MetaFilePath))
					{
						using (GZipStream unzipStream =
								new GZipStream(archiveFileStream, CompressionMode.Decompress))
						{
							unzipStream.CopyTo(unzippedFileStream);
						}
					}
				}

				config.CurrentMetaFileVersion = newMetaVersion;
				config.CurrentMetaFileDownloadTime = DateTime.Now;
				config.Save();
			}

			Log.Debug("Loading meta file");
			List<Deck> metaDecks = XmlManager<List<Deck>>.Load(MetaFilePath);
			Log.Info("Meta retrieved, " + metaDecks.Count + " decks loaded.");
			LogDeckClassCounts(metaDecks);
			return metaDecks;
		}

		private static void LogDeckClassCounts(IEnumerable<Deck> decks)
		{
			var classCounts = decks
				.GroupBy(deck => NormalizeClass(deck.Class))
				.OrderBy(group => group.Key)
				.Select(group => group.Key + "=" + group.Count());
			Log.Info("Meta deck class counts: " + string.Join(", ", classCounts));
		}

		private static async Task<string> RetrieveLegacyMetaVersion()
		{
			try
			{
				using (WebClient client = new WebClient())
				{
					return (await client.DownloadStringTaskAsync(MetaVersionUrl)).Trim();
				}
			}
			catch (Exception ex)
			{
				Log.Warn("Unable to retrieve legacy MetaStats version: " + ex.Message);
				return "";
			}
		}

		private List<Deck> LoadDeckCodeDecks()
		{
			var existingFiles = SelectDeckCodeFilePaths(DeckCodeFilePaths.Where(File.Exists));
			if (existingFiles.Count == 0)
			{
				Log.Info("No deck code file found in " + MetaCompanionPlugin.DataDirectory);
				return new List<Deck>();
			}
			Log.Info("Reading deck codes from preferred sources: " + string.Join(", ", existingFiles));

			HearthDb.Cards.LoadBaseData();

			var decks = new List<Deck>();
			var unknownCardDbfIds = new Dictionary<int, int>();
			var deckCodeEntries = existingFiles
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

		internal static List<string> SelectDeckCodeFilePaths(IEnumerable<string> existingFiles)
		{
			var existing = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
			var selected = new List<string>();
			if (existing.Contains(ManualDeckCodeFilePath))
			{
				selected.Add(ManualDeckCodeFilePath);
			}

			if (existing.Contains(HsReplayDeckCodeFilePath))
			{
				selected.Add(HsReplayDeckCodeFilePath);
				return selected;
			}

			if (existing.Contains(HsGuruDeckCodeFilePath))
			{
				selected.Add(HsGuruDeckCodeFilePath);
				return selected;
			}

			if (existing.Contains(ArchetypeBranchDeckCodeFilePath))
			{
				selected.Add(ArchetypeBranchDeckCodeFilePath);
			}
			return selected;
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

			var match = Regex.Match(line, @"AA[A-Za-z0-9+/=]+");
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

		internal class DeckCodeEntry
		{
			public string Code { get; set; }
			public string Name { get; set; }
		}
	}
}
