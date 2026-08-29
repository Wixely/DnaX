using System.Text.Json.Serialization;

namespace DnaX.RemoteAccess.Sqlite;

[JsonSerializable(typeof(string[]))]
internal sealed partial class DnaXRemoteAccessSqliteJsonContext : JsonSerializerContext
{
}
