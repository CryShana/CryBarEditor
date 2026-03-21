using System.Text.Json.Serialization;

using CryBar.Cli.Models;

namespace CryBar.Cli;

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(BarListResult[]))]
[JsonSerializable(typeof(BarInfoResult))]
[JsonSerializable(typeof(DepsResult))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(SearchResultJson[]))]
internal partial class CliJsonContext : JsonSerializerContext { }
