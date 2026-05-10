using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MediaTools.Presentation.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public event EventHandler<string>? NavigationRequested;

    public MainWindowViewModel()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionDisplay = v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    [ObservableProperty]
    private string _currentNavigationKey = "Dashboard";

    [ObservableProperty]
    private string _appVersionDisplay = "1.0";

    [RelayCommand]
    private void Navigate(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        CurrentNavigationKey = target;
        NavigationRequested?.Invoke(this, target);
    }
}
