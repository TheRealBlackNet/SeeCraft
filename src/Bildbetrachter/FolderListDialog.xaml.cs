using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Bildbetrachter;

/// <summary>Liste von bis zu 10 Zielordnern fuer "Verschieben ..." und "Kopieren ...".</summary>
public partial class FolderListDialog : Window
{
    public const int MaxFolders = 10;
    readonly AppSettings _s;

    public string? Selected { get; private set; }

    public FolderListDialog(AppSettings settings, string title)
    {
        InitializeComponent();
        _s = settings;
        Title = title;
        Refresh(_s.TargetFolders.Count > 0 ? 0 : -1);
    }

    void Refresh(int select = -1)
    {
        List.ItemsSource = null;
        List.ItemsSource = _s.TargetFolders.ToList();
        if (select >= 0 && select < _s.TargetFolders.Count) List.SelectedIndex = select;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_s.TargetFolders.Count >= MaxFolders)
        {
            MessageBox.Show(this, $"Es sind höchstens {MaxFolders} Ordner möglich.", Title);
            return;
        }
        var dlg = new OpenFolderDialog { Title = "Zielordner hinzufügen" };
        if (dlg.ShowDialog(this) != true) return;

        string dir = dlg.FolderName;
        if (!_s.TargetFolders.Contains(dir, StringComparer.OrdinalIgnoreCase))
            _s.TargetFolders.Add(dir);
        Refresh(_s.TargetFolders.Count - 1);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        int i = List.SelectedIndex;
        if (i < 0) return;
        _s.TargetFolders.RemoveAt(i);
        Refresh(Math.Min(i, _s.TargetFolders.Count - 1));
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        _s.TargetFolders.Sort(Native.StrCmpLogicalW);
        Refresh(0);
    }

    void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    void Move(int delta)
    {
        int i = List.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _s.TargetFolders.Count) return;
        (_s.TargetFolders[i], _s.TargetFolders[j]) = (_s.TargetFolders[j], _s.TargetFolders[i]);
        Refresh(j);
    }

    void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem)
            Accept();
    }

    void Accept()
    {
        if (List.SelectedItem is not string dir) return;
        if (!Directory.Exists(dir))
        {
            MessageBox.Show(this, "Dieser Ordner ist nicht verfügbar.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Selected = dir;
        DialogResult = true;
    }
}
