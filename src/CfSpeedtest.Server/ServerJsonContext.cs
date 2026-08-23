using System.Text.Json.Serialization;
using CfSpeedtest.Server.Services;

namespace CfSpeedtest.Server;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HuaweiDnsRecordSetRequest))]
internal partial class ServerJsonContext : JsonSerializerContext
{
}
