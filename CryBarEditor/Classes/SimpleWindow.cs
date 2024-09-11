using Avalonia.Controls;

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CryBarEditor.Classes;

public abstract class SimpleWindow : Window, INotifyPropertyChanged
{
    public new event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public void OnSelfChanged([CallerMemberName] string propertyName = "") => OnPropertyChanged(propertyName);
}
