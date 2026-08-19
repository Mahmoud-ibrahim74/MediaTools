namespace MediaTools.Presentation.ViewModels;

public enum FacebookQueueItemStatus
{
    Queued,
    FetchingInfo,
    Downloading,
    Muxing,
    Completed,
    Failed,
    Cancelled
}
