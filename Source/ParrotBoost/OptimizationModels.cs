using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ParrotBoost;

internal enum TweakSafety
{
    Safe,
    Moderate,
    Dangerous
}

internal enum TweakCategory
{
    System,
    Privacy,
    Performance
}

internal enum AppCategory
{
    Microsoft,
    AI,
    Games,
    ThirdParty
}

internal sealed class SystemTweak : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isApplied;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public TweakCategory Category { get; init; }
    public TweakSafety Safety { get; init; } = TweakSafety.Safe;
    public Action? RunAction { get; init; }
    public Action? RevertAction { get; init; }
    public Func<bool>? CheckAction { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public bool IsApplied
    {
        get => _isApplied;
        set
        {
            if (_isApplied == value) return;
            _isApplied = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class DebloatApp : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _isInstalled;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public AppCategory Category { get; init; }
    public string PackageName { get; init; } = "";
    public string WinGetId { get; init; } = "";
    public string WingetSource { get; init; } = "";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled == value) return;
            _isInstalled = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
