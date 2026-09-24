namespace UnnamedTrackingPlaynite;

internal sealed class SaveSyncStatus
{
    public string Status { get; }
    public int FileCount { get; }
    public bool UploadOnGameStop { get; }
    public bool DownloadOnGameStart { get; }

    public SaveSyncStatus(string status, int fileCount, bool uploadOnGameStop, bool downloadOnGameStart)
    {
        Status = status;
        FileCount = fileCount;
        UploadOnGameStop = uploadOnGameStop;
        DownloadOnGameStart = downloadOnGameStart;
    }
}
