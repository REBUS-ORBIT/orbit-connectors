namespace OrbitConnector.Rhino.Models;

/// <summary>
/// Server URLs and OAuth app IDs for prod and dev targets.
/// These are baked into the build — update if server URLs or app IDs change.
/// </summary>
public class ServerConfig
{
    public string ProdUrl   { get; set; } = "https://speckle.rebus.industries";
    public string DevUrl    { get; set; } = "https://speckle-dev.rebus.industries";
    public string ProdAppId { get; set; } = "c0c8e773a3";
    public string DevAppId  { get; set; } = "c047ac8afa";

    public static readonly ServerConfig Default = new();

    public string GetAppId(ServerTarget target) =>
        target == ServerTarget.Prod ? ProdAppId : DevAppId;

    public string GetUrl(ServerTarget target) =>
        target == ServerTarget.Prod ? ProdUrl : DevUrl;
}
