namespace CryBarEditor.Classes;

/// <summary>
/// Describes the export mode and settings chosen in the Advanced Export dialog.
/// </summary>
public class ExportOptions
{
    /// <summary>Export raw copy of the file (as-is from source)</summary>
    public bool Copy { get; set; }

    /// <summary>Export with format conversion (XMB→XML, DDT→TGA, etc.)</summary>
    public bool Convert { get; set; }

    /// <summary>Decompress files that have the Compressed flag before exporting</summary>
    public bool Decompress { get; set; }

    /// <summary>
    /// If true, files are exported directly to a user-chosen path (no relative folder structure).
    /// If false, relative paths from the game root are preserved under the export directory.
    /// </summary>
    public bool DirectExport { get; set; }

    /// <summary>
    /// The direct output directory chosen by the user (only used when DirectExport is true).
    /// </summary>
    public string? DirectExportPath { get; set; }

    /// <summary>Whether any file in the selection is compressed</summary>
    public bool AnyCompressed { get; set; }

    /// <summary>Export .mtl and textures alongside TMM→OBJ conversion</summary>
    public bool ExportMaterials { get; set; }

    /// <summary>Use glTF/GLB format instead of OBJ for TMM export</summary>
    public bool TmmToGltf { get; set; }

    /// <summary>Whether any file in the selection is convertible (XMB/DDT)</summary>
    public bool AnyConvertible { get; set; }

    /// <summary>Whether the user confirmed (OK) or cancelled the dialog</summary>
    public bool Confirmed { get; set; }
}
