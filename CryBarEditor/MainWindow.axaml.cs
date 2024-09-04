using System.IO;
using Avalonia.Controls;
using CryBar;

namespace CryBarEditor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        using var file = File.OpenRead(@"C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game\data\Data.bar");
        var bar = new BarFile(file);

        if (bar.Load())
        {
            var e = bar.Entries[0];
            var data = e.ReadDataDecompressed(file);
            
            BarFileEntry.ParseXMB(data.Span);
        }
    }
}