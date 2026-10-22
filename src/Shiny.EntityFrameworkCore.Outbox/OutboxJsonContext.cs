using System.Text.Json.Serialization;

namespace Shiny.EntityFrameworkCore.Outbox;

/// <summary>Source-generated metadata for the <see cref="OutboxMessage.Headers"/> column, so the mapping's value
/// converter needs no reflection-based serializer.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
sealed partial class OutboxJsonContext : JsonSerializerContext;
