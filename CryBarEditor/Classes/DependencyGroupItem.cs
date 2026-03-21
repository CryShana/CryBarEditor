using CryBar;
using CryBar.Dependencies;

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CryBarEditor.Classes;

public class DependencyGroupItem : INotifyPropertyChanged
{
    bool _isExpanded;

    public DependencyGroup Group { get; }
    public string DisplayName => Group.EntityName ?? "Ungrouped";
    public string EntityTypeLabel => Group.EntityType ?? "";
    public int ReferenceCount => Group.References.Count;
    public string ReferenceCountText => $"{ReferenceCount} ref{(ReferenceCount != 1 ? "s" : "")}";
    const int MaxGraphReferences = 50;
    public bool CanShowGraph => ReferenceCount <= MaxGraphReferences;
    public List<DependencyReference> References => Group.References;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public DependencyGroupItem(DependencyGroup group)
    {
        Group = group;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
