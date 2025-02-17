using System.Text.Json.Serialization;

namespace NAT64Config;

[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(PortForwardConfig))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class Nat64JsonContext : JsonSerializerContext
{
}
