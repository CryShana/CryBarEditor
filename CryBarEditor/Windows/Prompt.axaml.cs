using Avalonia;
using Avalonia.Controls;

using CryBarEditor.Classes;

namespace CryBarEditor;

public partial class Prompt : SimpleWindow
{
    public Prompt()
    {
        DataContext = this;
        InitializeComponent();
    }

    public Prompt(PromptType type, string title) : this()
    {
        Title = title;

        switch(type)
        {
            case PromptType.Information:
                break;

            case PromptType.Error:
                break;

            case PromptType.Success:
                break;

            case PromptType.Progress:
                break;
        }
    }
}

public enum PromptType
{
    Information,
    Error,
    Success,
    Progress
}