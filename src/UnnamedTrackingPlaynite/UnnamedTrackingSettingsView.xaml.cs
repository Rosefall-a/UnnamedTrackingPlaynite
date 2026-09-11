using System;
using System.Windows;
using System.Windows.Controls;

namespace UnnamedTrackingPlaynite;

public partial class UnnamedTrackingSettingsView : UserControl
{
    public UnnamedTrackingSettingsView()
    {
        InitializeComponent();
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(DataContext is UnnamedTrackingSettings settings))
        {
            UploadStatus.Text = "Unable to access plugin settings.";
            return;
        }

        UploadButton.IsEnabled = false;
        UploadStatus.Text = "Uploading Playnite library...";

        try
        {
            await settings.UploadLibraryAsync();
            UploadStatus.Text = "Library upload completed successfully.";
        }
        catch (Exception ex)
        {
            UploadStatus.Text = $"Upload failed: {ex.Message}";
        }
        finally
        {
            UploadButton.IsEnabled = true;
        }
    }
}
