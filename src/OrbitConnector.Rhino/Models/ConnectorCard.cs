using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace OrbitConnector.Rhino.Models;

[JsonConverter(typeof(StringEnumConverter))]
public enum CardType { Send, Receive }

[JsonConverter(typeof(StringEnumConverter))]
public enum LayerMode { All, ByLayer, Selection }

[JsonConverter(typeof(StringEnumConverter))]
public enum ServerTarget { Prod, Dev }

/// <summary>
/// A Send or Receive card. Persisted in RhinoDoc.Strings so cards travel with the file.
/// </summary>
public class ConnectorCard
{
    [JsonProperty("id")]          public string Id          { get; set; } = Guid.NewGuid().ToString();
    [JsonProperty("type")]        public CardType Type      { get; set; }
    [JsonProperty("target")]      public ServerTarget Target { get; set; } = ServerTarget.Prod;

    // Project / Model
    [JsonProperty("projectId")]   public string? ProjectId   { get; set; }
    [JsonProperty("projectName")] public string? ProjectName { get; set; }
    [JsonProperty("modelId")]     public string? ModelId     { get; set; }
    [JsonProperty("modelName")]   public string? ModelName   { get; set; }

    // Layer filtering (Send only)
    [JsonProperty("layerMode")]           public LayerMode    LayerMode           { get; set; } = LayerMode.All;
    [JsonProperty("includedLayers")]      public List<string> IncludedLayers      { get; set; } = new();
    // Snapshot of selected Rhino object GUIDs — captured when Selection mode is confirmed
    [JsonProperty("selectedObjectIds")]   public List<string> SelectedObjectIds   { get; set; } = new();

    // Send history
    [JsonProperty("lastVersionId")]  public string? LastVersionId  { get; set; }
    [JsonProperty("lastSentAt")]     public DateTime? LastSentAt   { get; set; }

    // Receive history
    [JsonProperty("pinnedVersionId")]    public string? PinnedVersionId    { get; set; }
    [JsonProperty("lastReceivedAt")]     public DateTime? LastReceivedAt   { get; set; }
    [JsonProperty("lastReceivedVersionId")] public string? LastReceivedVersionId { get; set; }

    /// <summary>Returns the server URL for this card based on target.</summary>
    public string ServerUrl(ServerConfig config) =>
        Target == ServerTarget.Prod ? config.ProdUrl : config.DevUrl;
}
