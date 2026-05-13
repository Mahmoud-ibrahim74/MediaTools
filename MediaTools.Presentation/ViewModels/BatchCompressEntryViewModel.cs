using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaTools.Presentation.ViewModels;

public partial class BatchCompressEntryViewModel : ObservableObject
{
    public BatchCompressEntryViewModel(string sourcePath)
    {
        SourcePath = sourcePath;
    }

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    /// <summary>Set when compression succeeds (for single-file result summary).</summary>
    public string? ProducedOutputPath { get; set; }

    [ObservableProperty]
    private BatchCompressEntryStatus _status = BatchCompressEntryStatus.Pending;

    [ObservableProperty]
    private string _detailMessage = string.Empty;
}
