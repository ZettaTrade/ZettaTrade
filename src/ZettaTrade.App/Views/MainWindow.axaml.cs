using Avalonia.ReactiveUI;
using ZettaTrade.App.ViewModels;

namespace ZettaTrade.App.Views;

public partial class MainWindow : ReactiveWindow<MainViewModel>
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
