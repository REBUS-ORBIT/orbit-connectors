using Newtonsoft.Json;
using Rhino;

namespace OrbitConnector.Rhino.Models;

/// <summary>
/// Persists connector cards in RhinoDoc.Strings so they travel with the .3dm file.
/// Uses a singleton pattern; call <see cref="LoadFromDocument"/> when a doc opens.
/// Fires <see cref="CardsChanged"/> when the card list is modified.
/// </summary>
public class CardStore
{
    private const string SECTION = "orbit_connector";
    private const string ENTRY   = "cards";

    public static CardStore? Instance { get; private set; }

    private readonly List<ConnectorCard> _cards = new();
    private RhinoDoc? _doc;

    public event EventHandler? CardsChanged;

    public static CardStore LoadFromDocument(RhinoDoc doc)
    {
        var store = new CardStore();
        store._doc = doc;
        store.Load();
        Instance = store;
        return store;
    }

    public IReadOnlyList<ConnectorCard> Cards => _cards.AsReadOnly();

    public void AddCard(ConnectorCard card)
    {
        _cards.Add(card);
        Save();
        CardsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateCard(ConnectorCard card)
    {
        var idx = _cards.FindIndex(c => c.Id == card.Id);
        if (idx >= 0) _cards[idx] = card;
        else _cards.Add(card);
        Save();
        CardsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveCard(string cardId)
    {
        _cards.RemoveAll(c => c.Id == cardId);
        Save();
        CardsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnDocumentClose(RhinoDoc doc)
    {
        if (_doc?.RuntimeSerialNumber == doc.RuntimeSerialNumber)
        {
            _cards.Clear();
            _doc = null;
        }
    }

    private void Load()
    {
        if (_doc == null) return;
        var json = _doc.Strings.GetValue(SECTION, ENTRY);
        if (string.IsNullOrWhiteSpace(json)) return;
        var loaded = JsonConvert.DeserializeObject<List<ConnectorCard>>(json);
        if (loaded != null)
        {
            _cards.Clear();
            _cards.AddRange(loaded);
        }
    }

    private void Save()
    {
        if (_doc == null) return;
        var json = JsonConvert.SerializeObject(_cards);
        _doc.Strings.SetString(SECTION, ENTRY, json);
    }
}
