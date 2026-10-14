using System.Windows;
using System.Windows.Controls;
using RSM.App.ViewModels;

namespace RSM.App.Views;

public partial class SitesView : UserControl
{
    public SitesView() => InitializeComponent();

    // PasswordBox.Password is deliberately not bindable; the value goes straight to the node VM (memory only).
    private void OnSqlPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: NodeEditorViewModel node } box)
        {
            node.SqlPassword = box.Password;
        }
    }

    private void OnRedisPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: NodeEditorViewModel node } box)
        {
            node.RedisPassword = box.Password;
        }
    }
}
