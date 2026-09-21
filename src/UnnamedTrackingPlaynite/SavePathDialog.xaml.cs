using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UnnamedTrackingPlaynite;

public partial class SavePathDialog : UserControl
{
    private readonly Func<string, string> _selectFolder;
    private readonly string _initialDirectory;
    private sealed class Row { public TextBox Name = null; public TextBox Path = null; }
    private readonly List<Row> _rows = new List<Row>();

    public bool Saved { get; private set; }
    public SavePathEntry[] Entries => _rows
        .Select(x => new SavePathEntry
        {
            Name = string.IsNullOrWhiteSpace(x.Name.Text) ? "Save location" : x.Name.Text.Trim(),
            Path = Environment.ExpandEnvironmentVariables(x.Path.Text.Trim())
        })
        .Where(x => !string.IsNullOrWhiteSpace(x.Path))
        .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
        .Select(x => x.First())
        .ToArray();

    public SavePathDialog(IEnumerable<SavePathEntry> entries, string initialDirectory, Func<string, string> selectFolder)
    {
        InitializeComponent();
        _selectFolder = selectFolder;
        _initialDirectory = initialDirectory;

        foreach (var entry in entries ?? Enumerable.Empty<SavePathEntry>())
            AddRow(entry.Name, entry.Path);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var selected = _selectFolder?.Invoke(_initialDirectory);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            var name = new DirectoryInfo(selected).Name;
            AddRow(string.IsNullOrWhiteSpace(name) ? "Save location" : name, selected ?? string.Empty);
        }
    }

    private void AddRow(string name, string path)
    {
        var nameBox = new TextBox { Text = name ?? "Save location", Margin = new Thickness(0, 0, 8, 8) };
        var pathBox = new TextBox { Text = path ?? "", Margin = new Thickness(0, 0, 8, 8) };
        var remove = new Button { Content = "Remove", Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6, 2, 6, 2) };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        Grid.SetColumn(nameBox, 0);
        Grid.SetColumn(pathBox, 1);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(nameBox);
        grid.Children.Add(pathBox);
        grid.Children.Add(remove);
        EntriesPanel.Children.Add(grid);
        _rows.Add(new Row { Name = nameBox, Path = pathBox });

        remove.Click += (sender, args) =>
        {
            EntriesPanel.Children.Remove(grid);
            _rows.RemoveAll(x => ReferenceEquals(x.Name, nameBox));
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Saved = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Saved = true;
        Close();
    }

    private void Close()
    {
        var window = Window.GetWindow(this);
        if (window != null) window.DialogResult = Saved;
    }
}
