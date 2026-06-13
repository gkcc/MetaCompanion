using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker.Hearthstone;

namespace MetaCompanion
{
	public interface IMetaRetriever
	{
		// Returns the list of all decks used in the current meta.
		// Returns an empty list when no local deck snapshot is available yet.
		Task<List<Deck>> RetrieveMetaDecks(PluginConfig config);
	}
}
