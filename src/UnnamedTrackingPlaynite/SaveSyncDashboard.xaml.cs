using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace UnnamedTrackingPlaynite;

public partial class SaveSyncDashboard : UserControl
{
    private sealed class Row
    {
        public string GameName { get; set; } = "";
        public string Status { get; set; } = "";
        public int FileCount { get; set; }
        public string Upload { get; set; } = "";
        public string Download { get; set; } = "";
        public string Locations { get; set; } = "";
    }

    public SaveSyncDashboard(IPlayniteAPI api, SaveSyncManager manager)
    {
        InitializeComponent();
        var rows = new List<Row>();
        foreach (var game in api.Database.Games.OrderBy(x => x.Name))
        {
            var config = manager.Configuration(game.Id);
            var status = manager.GetStatus(game);
            rows.Add(new Row
            {
                GameName = game.Name,
                Status = status.Status,
                FileCount = status.FileCount,
                Upload = status.UploadOnGameStop ? "Enabled" : "Disabled",
                Download = status.DownloadOnGameStart ? "Enabled" : "Disabled",
                Locations = config.SavePaths.Count == 0
                    ? "None configured"
                    : string.Join(", ", config.SavePaths.Select(x => x.Name))
            });
        }
        GamesList.ItemsSource = rows;
    }
}
