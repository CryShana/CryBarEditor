using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CryBarEditor.Classes;

public class Configuration
{
    public string? RootDirectory { get; set; }
    public string? ExportRootDirectory { get; set; }
    public string? BarFile { get; set; }
    public string? LastVersionCheck { get; set; }
    public string? SearchExclusionFilter { get; set; }
    public bool? SearchIsInclusionFilter { get; set; }
    public bool? SearchCaseSensitive { get; set; }
    public bool? SearchUseRegex { get; set; }
    public bool? SearchFilesOnly { get; set; }
    public bool? ExportDoCopy { get; set; }
    public bool? ExportDoConvert { get; set; }
    public bool? ExportDoDecompress { get; set; }
    public bool? ExportDoExportMaterials { get; set; }
    public bool? ExportTmmToGltf { get; set; }
    public bool? ExportAnimations { get; set; }
    public bool? ExportEmitFbximport { get; set; }
    public bool? ExportAllEventVariants { get; set; }
    public bool? ExportRetainSubfolders { get; set; }
    public string? EditorCommand { get; set; }
    public bool? ExportOpenInEditor { get; set; }
    public string? StringTableLanguage { get; set; }
    public List<QuickAccessEntry>? QuickAccessEntries { get; set; }
    public bool? FilterOnlyOverriddenFiles { get; set; }
    public bool? FilterOnlyOverriddenBarEntries { get; set; }
    public bool? Show3DMarkers { get; set; }
    public bool? Show3DGroundGrid { get; set; }
    public bool? Show3DTextured { get; set; }
    public bool? ShowScenarioEntities { get; set; }
    public bool? ShowScenarioWater { get; set; }
    public string? ScenarioLastSaveDirectory { get; set; }
    public double? ScreenshotScaleFactor { get; set; }
    public bool? ScreenshotTransparent { get; set; }
    public string? ScreenshotFormat { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Configuration))]
[JsonSerializable(typeof(List<QuickAccessEntry>))]
public partial class CryBarJsonContext : JsonSerializerContext { }
