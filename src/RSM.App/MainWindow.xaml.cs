using System.Windows;
using RSM.App.ViewModels;

namespace RSM.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // A minimised window pauses live collection: no load on the customer's servers while nobody is looking.
        StateChanged += (_, _) => viewModel.Live.SetMinimized(WindowState == WindowState.Minimized);
    }
}
