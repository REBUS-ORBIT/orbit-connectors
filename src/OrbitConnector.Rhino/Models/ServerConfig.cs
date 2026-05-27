namespace OrbitConnector.Rhino.Models;

/// <summary>
/// Server URLs and OAuth app IDs for prod and dev targets.
/// These are baked into the build — update if server URLs or app IDs change.
/// </summary>
public class ServerConfig
{
    public string ProdUrl       { get; set; } = "https://orbit.rebus.industries";
    public string DevUrl        { get; set; } = "https://orbit-dev.rebus.industries";
    public string ProdAppId     { get; set; } = "v0bdx6giq4";
    public string DevAppId      { get; set; } = "fozz1jpigz";
    public string ProdAppSecret { get; set; } = "sw5jmwk6bi";
    public string DevAppSecret  { get; set; } = "cf20v65k44";

    public static readonly ServerConfig Default = new();

    public string GetAppId(ServerTarget target) =>
        target == ServerTarget.Prod ? ProdAppId : DevAppId;

    public string GetAppSecret(ServerTarget target) =>
        target == ServerTarget.Prod ? ProdAppSecret : DevAppSecret;

    public string GetUrl(ServerTarget target) =>
        target == ServerTarget.Prod ? ProdUrl : DevUrl;
}
