using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace UnnamedTrackingPlaynite;

internal partial class SavePathDialog : UserControl
{
    public bool Saved { get; private set; }
    public string[] Paths => PathsBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => Environment.ExpandEnvironmentVariables(x.Trim()))
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public SavePathDialog(string[] paths)
    {
        InitializeComponent();
        PathsBox.Text = string.Join(Environment.NewLine, paths ?? Array.Empty<string>());
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
