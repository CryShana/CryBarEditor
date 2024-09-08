﻿using System.Text.Json.Serialization;

namespace CryBarEditor.Classes;

public class Configuration
{
    public string? RootDirectory { get; set; }
    public string? ExportRootDirectory { get; set; } 
}

[JsonSerializable(typeof(Configuration))]
public partial class CryBarJsonContext : JsonSerializerContext { }