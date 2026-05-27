using Newtonsoft.Json;

namespace Orbit.Sdk.Api.Models;

public class OrbitUser
{
    [JsonProperty("id")]     public string? Id     { get; set; }
    [JsonProperty("name")]   public string? Name   { get; set; }
    [JsonProperty("email")]  public string? Email  { get; set; }
    [JsonProperty("avatar")] public string? Avatar { get; set; }
}

public class OrbitProject
{
    [JsonProperty("id")]          public string? Id          { get; set; }
    [JsonProperty("name")]        public string? Name        { get; set; }
    [JsonProperty("description")] public string? Description { get; set; }
    [JsonProperty("updatedAt")]   public DateTime? UpdatedAt  { get; set; }
}

public class OrbitModel
{
    [JsonProperty("id")]        public string? Id        { get; set; }
    [JsonProperty("name")]      public string? Name      { get; set; }
    [JsonProperty("updatedAt")] public DateTime? UpdatedAt { get; set; }
}

public class OrbitVersion
{
    [JsonProperty("id")]                  public string? Id                  { get; set; }
    [JsonProperty("message")]             public string? Message             { get; set; }
    [JsonProperty("referencedObject")]    public string? ReferencedObject    { get; set; }
    [JsonProperty("sourceApplication")]   public string? SourceApplication   { get; set; }
    [JsonProperty("createdAt")]           public DateTime? CreatedAt          { get; set; }
}
