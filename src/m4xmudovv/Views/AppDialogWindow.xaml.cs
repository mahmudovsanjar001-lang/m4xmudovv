using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace m4xmudovv.Views;

/// <summary>One row of the "Add to Playlist" dialog.</summary>
public sealed class DialogChoice : INotifyPropertyChanged
{
    private bool _isChecked;

    public string Name { get; init; } = string.Empty;
    public string Detail { get; set; } = string.Empty;

    /// <summary>The object this row stands for (an existing playlist), or null for a playlist that is created on Save.</summary>
    public object? Tag { get; init; }

    public bool IsNew => Tag is null;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The one themed dialog used for confirmations, text prompts and playlist selection, so every dialog
/// matches the rest of the application (dark/light theme, same buttons and switches).
/// </summary>
public partial class AppDialogWindow : Window
{
    private Func<string, string?>? _validate;
    private Func<Window, DialogChoice?>? _createChoice;

    private AppDialogWindow(Window? owner, string title, string message, string confirmText)
    {
        InitializeComponent();
        if (owner is not null && owner.IsVisible) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.Content = confirmText;
    }

    /// <summary>Yes/No question. Returns true when the user confirmed.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText)
    {
        var dialog = new AppDialogWindow(owner, title, message, confirmText);
        return dialog.ShowDialog() == true;
    }

    /// <summary>Asks for a line of text. Returns the trimmed text, or null if cancelled. <paramref name="validate"/> returns an error message or null.</summary>
    public static string? Prompt(Window? owner, string title, string message, string initialText, string confirmText, Func<string, string?> validate)
    {
        var dialog = new AppDialogWindow(owner, title, message, confirmText) { _validate = validate };
        dialog.InputFrame.Visibility = Visibility.Visible;
        dialog.InputBox.Text = initialText;
        dialog.Loaded += (_, _) =>
        {
            dialog.InputBox.Focus();
            dialog.InputBox.SelectAll();
        };
        dialog.UpdateValidation();
        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
    }

    /// <summary>
    /// Shows switches for the given choices (and a "New playlist" button when <paramref name="createChoice"/> is given).
    /// Returns true if the user saved; the caller then reads the IsChecked state of every choice.
    /// </summary>
    public static bool Choose(Window? owner, string title, string message, ObservableCollection<DialogChoice> choices,
                              Func<Window, DialogChoice?>? createChoice, string confirmText)
    {
        var dialog = new AppDialogWindow(owner, title, message, confirmText) { _createChoice = createChoice };
        dialog.ChoicesList.ItemsSource = choices;
        dialog.ChoicesScroll.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        choices.CollectionChanged += (_, _) =>
            dialog.ChoicesScroll.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        dialog.NewItemButton.Visibility = createChoice is null ? Visibility.Collapsed : Visibility.Visible;
        return dialog.ShowDialog() == true;
    }

    private void UpdateValidation()
    {
        if (_validate is null) return;
        var text = InputBox.Text.Trim();
        var error = text.Length == 0 ? "Enter a name." : _validate(text);
        ErrorText.Text = error ?? string.Empty;
        // An empty box is not an error worth shouting about; just keep the button disabled.
        ErrorText.Visibility = error is null || text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.IsEnabled = error is null;
    }

    private void InputBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConfirmButton is not null) UpdateValidation();
    }

    private void NewItem_OnClick(object sender, RoutedEventArgs e)
    {
        var created = _createChoice?.Invoke(this);
        if (created is null) return;
        created.IsChecked = true;
        ((ObservableCollection<DialogChoice>)ChoicesList.ItemsSource).Add(created);
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled) return;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Title_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
