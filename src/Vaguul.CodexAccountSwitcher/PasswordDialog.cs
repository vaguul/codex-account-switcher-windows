using System.Windows;
using System.Windows.Controls;

namespace Vaguul.CodexAccountSwitcher;

public sealed class PasswordDialog : Window
{
    private readonly PasswordBox _passwordBox = new() { MinWidth = 300 };
    private readonly PasswordBox? _confirmationBox;
    private readonly TextBlock _errorText = new() { Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 166, 166)), Height = 22 };
    private char[]? _acceptedPassword;

    public PasswordDialog(string title, string prompt, bool confirmPassword)
    {
        Title = title;
        Width = 410;
        Height = confirmPassword ? 300 : 250;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 28, 31));
        Foreground = System.Windows.Media.Brushes.White;

        var accept = new System.Windows.Controls.Button { Content = "Continue", IsDefault = true };
        accept.Click += (_, _) => Accept(confirmPassword);
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        actions.Children.Add(accept);
        actions.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Password", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        content.Children.Add(_passwordBox);
        if (confirmPassword)
        {
            content.Children.Add(new TextBlock { Text = "Confirm password", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
            _confirmationBox = new PasswordBox { MinWidth = 300 };
            content.Children.Add(_confirmationBox);
        }

        content.Children.Add(_errorText);
        content.Children.Add(actions);
        Content = content;
        Loaded += (_, _) => _passwordBox.Focus();
        Closed += (_, _) =>
        {
            _passwordBox.Clear();
            _confirmationBox?.Clear();
        };
    }

    public char[] CopyPassword()
    {
        var result = _acceptedPassword?.ToArray() ?? [];
        if (_acceptedPassword is not null)
        {
            Array.Clear(_acceptedPassword, 0, _acceptedPassword.Length);
            _acceptedPassword = null;
        }

        return result;
    }

    private void Accept(bool confirmPassword)
    {
        if (_passwordBox.Password.Length < 8)
        {
            _errorText.Text = "Use at least 8 characters.";
            _passwordBox.Focus();
            return;
        }

        if (confirmPassword && _confirmationBox?.Password != _passwordBox.Password)
        {
            _errorText.Text = "The passwords do not match.";
            _confirmationBox?.Focus();
            return;
        }

        _acceptedPassword = _passwordBox.Password.ToCharArray();
        DialogResult = true;
    }
}
