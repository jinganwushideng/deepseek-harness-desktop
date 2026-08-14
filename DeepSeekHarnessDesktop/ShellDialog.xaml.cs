using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeepSeekHarnessDesktop;

public partial class ShellDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;
    private readonly MessageBoxResult _fallback;

    private ShellDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        _fallback = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.YesNoCancel or MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
        ConfigureIcon(image);
        ConfigureButtons(buttons);
        SourceInitialized += (_, _) => ApplyDwmAppearance();
    }

    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
        Show(null, message, title, buttons, image);

    public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new ShellDialog(message, title, buttons, image);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result == MessageBoxResult.None ? dialog._fallback : dialog._result;
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        MessageIcon.Data = image switch
        {
            MessageBoxImage.Error => Geometry.Parse("M12,3 C17,3 21,7 21,12 C21,17 17,21 12,21 C7,21 3,17 3,12 C3,7 7,3 12,3 Z M8.5,8.5 L15.5,15.5 M15.5,8.5 L8.5,15.5"),
            MessageBoxImage.Warning => Geometry.Parse("M12,3 L22,21 L2,21 Z M12,9 L12,14 M12,17 L12,17.1"),
            MessageBoxImage.Question => Geometry.Parse("M8.5,9 C8.8,6.8 10.4,5.5 12.5,5.5 C14.8,5.5 16.5,7 16.5,9 C16.5,11.8 12,11.5 12,15 M12,18 L12,18.1"),
            _ => Geometry.Parse("M12,3 C17,3 21,7 21,12 C21,17 17,21 12,21 C7,21 3,17 3,12 C3,7 7,3 12,3 Z M12,8 L12,8.1 M12,11 L12,17")
        };
        var colorKey = image switch
        {
            MessageBoxImage.Error => "DangerText",
            MessageBoxImage.Warning => "WarningText",
            _ => "Accent"
        };
        MessageIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, colorKey);
        MessageIconPanel.SetResourceReference(BackgroundProperty, image switch
        {
            MessageBoxImage.Error => "DangerPanelBg",
            MessageBoxImage.Warning => "WarningBadgeBg",
            _ => "InfoIconBg"
        });
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                AddButton("取消", MessageBoxResult.Cancel, false, true);
                AddButton("确定", MessageBoxResult.OK, true, false);
                break;
            case MessageBoxButton.YesNo:
                AddButton("否", MessageBoxResult.No, false, true);
                AddButton("是", MessageBoxResult.Yes, true, false);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("取消", MessageBoxResult.Cancel, false, true);
                AddButton("否", MessageBoxResult.No, false, false);
                AddButton("是", MessageBoxResult.Yes, true, false);
                break;
            default:
                AddButton("确定", MessageBoxResult.OK, true, true);
                break;
        }
    }

    private void AddButton(string label, MessageBoxResult result, bool primary, bool cancel)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = label,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = primary,
            IsCancel = cancel,
            Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton")
        };
        button.Click += (_, _) => { _result = result; DialogResult = true; };
        ButtonsPanel.Children.Add(button);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) { _result = _fallback; Close(); }
    private void Dialog_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape) { _result = _fallback; Close(); } }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }

    private void ApplyDwmAppearance()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var corner = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        var background = System.Windows.Application.Current.TryFindResource("Bg") as SolidColorBrush;
        var dark = background is null || background.Color.R + background.Color.G + background.Color.B < 384 ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
