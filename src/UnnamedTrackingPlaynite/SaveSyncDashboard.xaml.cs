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

    internal SaveSyncDashboard(IPlayniteAPI api, SaveSyncManager manager, Action<Game> configure, Func<Game, bool, Task> sync)
    {
        InitializeComponent();
        this.api = api;
        this.manager = manager;
        this.configure = configure;
        this.sync = sync;
        RefreshRows();
    }

    private void RefreshRows()
    {
        rows = api.Database.Games.OrderBy(x => x.Name).Select(game =>
        {
            var config = manager.Configuration(game.Id);
            var status = manager.GetStatus(game);
            return new Row
            {
                Game = game,
                GameName = game.Name ?? "<unnamed>",
                Status = status.Status,
                FileCount = status.FileCount,
                Upload = status.UploadOnGameStop ? "Enabled" : "Disabled",
                Download = status.DownloadOnGameStart ? "Enabled" : "Disabled",
                Locations = config.SavePaths.Count == 0
                    ? "None configured"
                    : string.Join(" | ", config.SavePaths.Select(x =>
                        (string.IsNullOrWhiteSpace(x.Name) ? "Save location" : x.Name) + ": " + x.Path))
            };
        }).ToList();

        SummaryText.Text = rows.Count + " game(s) in library • " +
                           rows.Count(x => x.Status != "Not configured") + " configured";
        selectedRow = null;
        UpdateActionButtons();
        RebuildGroups();
    }

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
                    await sync(row.Game, true);
                    RefreshRows();
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
            await sync(row.Game, true);
            RefreshRows();
        }));
        menu.Items.Add(CreateMenuItem("Download latest", async () =>
        {
            await sync(row.Game, false);
            RefreshRows();
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
        await sync(selectedRow.Game, true);
        RefreshRows();
    }

    private async void SyncDownload_Click(object sender, RoutedEventArgs e)
    {
        if (selectedRow == null) return;
        await sync(selectedRow.Game, false);
        RefreshRows();
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