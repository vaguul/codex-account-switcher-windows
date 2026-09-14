using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vaguul.CodexAccountSwitcher;

public sealed class AccountNameDialog : Window
{
    private readonly System.Windows.Controls.TextBox _nameBox = new() { MinWidth = 300, MaxLength = 60, Margin = new Thickness(0, 8, 0, 18) };
    private readonly TextBlock _errorText = new() { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 166, 166)), Height = 20 };

    public AccountNameDialog(string title = "Save active account")
    {
        Title = title;
        Width = 390;
        Height = 240;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 28, 31));
        Foreground = System.Windows.Media.Brushes.White;

        var save = new System.Windows.Controls.Button { Content = "Save account", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_nameBox.Text))
            {
                _errorText.Text = "Enter a name to continue.";
                _nameBox.Focus();
                return;
            }

            DialogResult = true;
        };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        actions.Children.Add(save);
        actions.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "Account name", FontWeight = FontWeights.SemiBold });
        content.Children.Add(_nameBox);
        content.Children.Add(_errorText);
        content.Children.Add(actions);
        Content = content;
    }

    public string AccountName => _nameBox.Text.Trim();
}
