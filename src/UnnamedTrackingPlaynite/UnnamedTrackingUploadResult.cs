using System;
using System.Collections.Generic;

namespace UnnamedTrackingPlaynite;

public sealed class UnnamedTrackingUploadFailure
{
    public string GameName { get; set; } = string.Empty;
    public Guid? GameId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
}

public sealed class UnnamedTrackingUploadResult
{
    public int TotalGames { get; set; }
    public int SucceededGames { get; set; }
    public List<UnnamedTrackingUploadFailure> Failures { get; set; } = new List<UnnamedTrackingUploadFailure>();
    public List<UnnamedTrackingUploadFailure> Warnings { get; set; } = new List<UnnamedTrackingUploadFailure>();

    public int FailedGames => Failures.Count;
    public int WarningCount => Warnings.Count;
}
