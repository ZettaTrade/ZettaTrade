using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using ReactiveUI;
using ZettaTrade.App.ViewModels;

namespace ZettaTrade.App.Views;

public partial class MainWindow : ReactiveWindow<MainViewModel>
{
    public MainWindow()
    {
        InitializeComponent();

        // ReactiveUI activation
        this.WhenActivated(_ =>
        {
            // Called when window becomes active
        });
    }
}
