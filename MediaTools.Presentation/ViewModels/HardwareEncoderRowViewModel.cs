using CommunityToolkit.Mvvm.ComponentModel;
using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.ViewModels;

public partial class HardwareEncoderRowViewModel : ObservableObject
{
    public VideoHardwareEncoderKind Kind { get; init; }

    public string Title { get; init; } = string.Empty;

    public string FullLabel { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isAvailable;
}
