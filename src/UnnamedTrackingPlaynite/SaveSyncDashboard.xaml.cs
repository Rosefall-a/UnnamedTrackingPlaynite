using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace UnnamedTrackingPlaynite;

public partial class SaveSyncDashboard : UserControl
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public SaveGameConfiguration Configuration { get; set; } = null!;
        public Game Game { get; set; } = null!;
        public string GameName { get; set; } = "";
        public string Status { get; set; } = "";
        public int FileCount { get; set; }
        public string Upload { get; set; } = "";
        public string Download { get; set; } = "";
        public string Locations { get; set; } = "";
    }

    private readonly IPlayniteAPI api;
    private readonly SaveSyncManager manager;
    private readonly Action<Game> configure;
    private readonly Func<Game, bool, Task> sync;
    private readonly List<Expander> expanders = new List<Expander>();
    private List<Row> rows = new List<Row>();
    private Row? selectedRow;
    private readonly Action openApplication;
    private readonly Action cancel;
    private int refreshVersion;

    internal SaveSyncDashboard(IPlayniteAPI api, SaveSyncManager manager, Action<Game> configure, Func<Game, bool, Task> sync, Action openApplication, Action cancel)
    {
        InitializeComponent();
        this.api = api;
        this.manager = manager;
        this.configure = configure;
        this.sync = sync;
        this.openApplication = openApplication;
        this.cancel = cancel;
        RefreshRows();
    }

    private async void RefreshRows()
    {
        var version = ++refreshVersion;
        // SDK reads happen before Task.Run; hashing and filesystem scans use copies.
        var captured = api.Database.Games.OrderBy(game => game.Name).Select(game => new Row
        {
            Id = game.Id,
            Game = game,
            GameName = game.Name ?? "<unnamed>",
            Configuration = manager.Configuration(game.Id)
        }).ToList();
        SummaryText.Text = "Checking local save files...";
        await Task.Run(() =>
        {
            foreach (var row in captured)
            {
                var config = row.Configuration;
                try
                {
                    var status = manager.GetStatus(config);
                    row.Status = status.Status; row.FileCount = status.FileCount;
                }
                catch (Exception ex) { row.Status = "Local save error: " + ex.Message; }
                row.Upload = config.UploadOnGameStop ? "Enabled" : "Disabled";
                row.Download = config.DownloadOnGameStart ? "Enabled" : "Disabled";
                row.Locations = config.SavePaths.Count == 0 ? "None configured" :
                    string.Join(" | ", config.SavePaths.Select(path => path.Name + ": " + path.Path));
            }
        });
        if (version != refreshVersion) return;
        rows = captured;
        SummaryText.Text = rows.Count + " game(s) in library • " + rows.Count(row => row.Status != "Not configured") + " configured";
        selectedRow = null;
        UpdateActionButtons();
        RebuildGroups();
    }

    private async Task RunSyncAsync(Game game, bool upload)
    {
        GroupsPanel.IsEnabled = false;
        UploadButton.IsEnabled = DownloadButton.IsEnabled = ConfigureButton.IsEnabled = false;
        SummaryText.Text = upload ? "Uploading saves..." : "Checking cloud saves...";
        try { await sync(game, upload); }
        finally { GroupsPanel.IsEnabled = true; RefreshRows(); }
    }

    private void OpenApplication_Click(object sender, RoutedEventArgs e) => openApplication();
    private void CancelSync_Click(object sender, RoutedEventArgs e) => cancel();

    private IEnumerable<Row> SortedRows(IEnumerable<Row> source)
    {
        var mode = (SortCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Status";
        if (mode == "Game name") return source.OrderBy(x => x.GameName, StringComparer.OrdinalIgnoreCase);
        if (mode == "File count") return source.OrderByDescending(x => x.FileCount).ThenBy(x => x.GameName, StringComparer.OrdinalIgnoreCase);
        return source.OrderBy(x => StatusOrder(x.Status)).ThenBy(x => x.GameName, StringComparer.OrdinalIgnoreCase);
    }

    private static int StatusOrder(string status)
    {
        switch (status)
        {
            case "Local changes pending": return 0;
            case "Up to date": return 1;
            case "No local save files": return 2;
            case "Not configured": return 3;
            default: return 4;
        }
    }

    private void RebuildGroups()
    {
        GroupsPanel.Children.Clear();
        expanders.Clear();

        var grouped = SortedRows(rows).GroupBy(x => x.Status).OrderBy(x => StatusOrder(x.Key));
        foreach (var group in grouped)
        {
            var expander = new Expander
            {
                Header = group.Key + " (" + group.Count() + ")",
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var list = new ListView
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ItemsSource = group.ToList(),
                SelectionMode = SelectionMode.Single
            };
            list.SelectionChanged += (s, e) =>
            {
                if (list.SelectedItem is Row row) selectedRow = row;
                UpdateActionButtons();
            };

            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "Game", DisplayMemberBinding = new System.Windows.Data.Binding("GameName"), Width = 190 });
            view.Columns.Add(new GridViewColumn { Header = "Files", DisplayMemberBinding = new System.Windows.Data.Binding("FileCount"), Width = 55 });
            view.Columns.Add(new GridViewColumn { Header = "Upload", DisplayMemberBinding = new System.Windows.Data.Binding("Upload"), Width = 80 });
            view.Columns.Add(new GridViewColumn { Header = "Download", DisplayMemberBinding = new System.Windows.Data.Binding("Download"), Width = 85 });
            view.Columns.Add(new GridViewColumn { Header = "Save locations", DisplayMemberBinding = new System.Windows.Data.Binding("Locations"), Width = 520 });
            list.View = view;

            var menu = new ContextMenu();
            menu.Opened += (s, e) =>
            {
                if (list.SelectedItem is Row row) BuildContextMenu(menu, row);
            };
            list.ContextMenu = menu;
            list.MouseDoubleClick += async (s, e) =>
            {
                if (list.SelectedItem is Row row)
                {
                    await RunSyncAsync(row.Game, true);
                }
            };

            expander.Content = list;
            expanders.Add(expander);
            GroupsPanel.Children.Add(expander);
        }
    }

    private void BuildContextMenu(ContextMenu menu, Row row)
    {
        menu.Items.Clear();
        menu.Items.Add(CreateMenuItem("Sync / upload now", async () =>
        {
            await RunSyncAsync(row.Game, true);
        }));
        menu.Items.Add(CreateMenuItem("Download latest", async () =>
        {
            await RunSyncAsync(row.Game, false);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Change save locations & configuration", () =>
        {
            configure(row.Game);
            RefreshRows();
        }));
    }

    private static MenuItem CreateMenuItem(string text, Func<Task> action)
    {
        var item = new MenuItem { Header = text };
        item.Click += async (s, e) => await action();
        return item;
    }

    private static MenuItem CreateMenuItem(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (s, e) => action();
        return item;
    }

    private void UpdateActionButtons()
    {
        var enabled = selectedRow != null;
        UploadButton.IsEnabled = enabled;
        DownloadButton.IsEnabled = enabled;
        ConfigureButton.IsEnabled = enabled;
    }

    private async void SyncUpload_Click(object sender, RoutedEventArgs e)
    {
        if (selectedRow == null) return;
        await RunSyncAsync(selectedRow.Game, true);
    }

    private async void SyncDownload_Click(object sender, RoutedEventArgs e)
    {
        if (selectedRow == null) return;
        await RunSyncAsync(selectedRow.Game, false);
    }

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        if (selectedRow == null) return;
        configure(selectedRow.Game);
        RefreshRows();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupsPanel != null && IsInitialized) RebuildGroups();
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var expander in expanders) expander.IsExpanded = true;
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var expander in expanders) expander.IsExpanded = false;
    }
}
