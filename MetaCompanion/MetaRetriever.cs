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
				Log.Info("Meta retrieved from deck code file, " + deckCodeDecks.Count + " decks loaded.");
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
			Log.Info("Meta retrieved, " + metaDecks.Count + " decks loaded.");
			LogDeckClassCounts(metaDecks);
			return Task.FromResult(metaDecks);
		}

		private static void LogDeckClassCounts(IEnumerable<Deck> decks)
		{
			var classCounts = decks
				.GroupBy(deck => NormalizeClass(deck.Class))
				.OrderBy(group => group.Key)
				.Select(group => group.Key + "=" + group.Count());
			Log.Info("Meta deck class counts: " + string.Join(", ", classCounts));
		}

		private List<Deck> LoadDeckCodeDecks()
		{
			return LoadDeckCodeDecks(MetaCompanionPlugin.DataDirectory);
		}

		internal static List<Deck> LoadDeckCodeDecks(string dataDirectory)
		{
			var deckCodeFilePaths = BuildDeckCodeFilePaths(dataDirectory);
			var existingFiles = SelectDeckCodeFilePaths(
				deckCodeFilePaths.Where(File.Exists),
				dataDirectory);
			if (existingFiles.Count == 0)
			{
				Log.Info("No deck code file found in " + dataDirectory);
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

		private static string[] BuildDeckCodeFilePaths(string dataDirectory)
		{
			return new[]
			{
				Path.Combine(dataDirectory, "deckcodes.txt"),
				Path.Combine(dataDirectory, "hsreplay_deckcodes.txt"),
				Path.Combine(dataDirectory, "hsguru_deckcodes.txt"),
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
			var archetypeBranchDeckCodeFilePath = Path.Combine(dataDirectory, "archetype_deck_branches.tsv");
			if (existing.Contains(manualDeckCodeFilePath))
			{
				selected.Add(manualDeckCodeFilePath);
			}

			if (existing.Contains(hsReplayDeckCodeFilePath))
			{
				selected.Add(hsReplayDeckCodeFilePath);
				return selected;
			}

			if (existing.Contains(hsGuruDeckCodeFilePath))
			{
				selected.Add(hsGuruDeckCodeFilePath);
				return selected;
			}

			if (existing.Contains(archetypeBranchDeckCodeFilePath))
			{
				selected.Add(archetypeBranchDeckCodeFilePath);
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
