using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DeskBox.Views;

public partial class NordConfirmDialog : Window
{
    public NordConfirmDialog(
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消",
        bool isDanger = false)
    {
        InitializeComponent();
        TxtTitle.Text = title;
        Title = title;
        TxtMessage.Text = message;
        BtnConfirm.Content = confirmText;
        BtnCancel.Content = cancelText;

        if (isDanger)
        {
            BtnConfirm.Style = (Style)FindResource("NordDangerButtonStyle");
            IconBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "NordDangerTintBrush");
            IconPath.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "NordDangerBrush");
        }
    }

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消",
        bool isDanger = false)
    {
        var dialog = new NordConfirmDialog(title, message, confirmText, cancelText, isDanger);
        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        return dialog.ShowDialog() == true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            BtnCancel_Click(sender, e);
        }
    }
}
