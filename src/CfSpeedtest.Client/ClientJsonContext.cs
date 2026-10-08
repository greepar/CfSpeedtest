using System.Text.Json.Serialization;
using CfSpeedtest.Shared;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClientRegisterRequest))]
[JsonSerializable(typeof(ApiResponse<ClientRegisterResponse>))]
[JsonSerializable(typeof(ApiResponse<SpeedTestTask>))]
[JsonSerializable(typeof(SpeedTestReport))]
[JsonSerializable(typeof(ApiResponse<ClientUpdateInfo>))]
[JsonSerializable(typeof(AdditionalIpBatchRequest))]
[JsonSerializable(typeof(ApiResponse<AdditionalIpBatchResponse>))]
[JsonSerializable(typeof(ClientHeartbeatRequest))]
[JsonSerializable(typeof(ApiResponse<ClientHeartbeatResponse>))]
[JsonSerializable(typeof(ClientWsMessage))]
internal partial class ClientJsonContext : JsonSerializerContext;
