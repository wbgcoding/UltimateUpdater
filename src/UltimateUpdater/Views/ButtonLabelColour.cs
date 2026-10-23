using System.Windows;
using System.Windows.Documents;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace UltimateUpdater.Views;

/// <summary>
/// Keeps a button's label and icon the colour they are meant to be while the button is held down.
/// WPF-UI's button template reacts to a press with
/// <c>&lt;Setter Property="TextElement.Foreground" Value="{Binding PressedForeground, RelativeSource=
/// TemplatedParent}"/&gt;</c>, but on a rendered pressed control that binding does not deliver: the button
/// ends up with TextElement.Foreground at its plain black default, and label/icon inherit that black
/// regardless of what PressedForeground itself reads. A LOCAL value on the element outranks a template
/// trigger, so this sets one; as a resource reference rather than a fixed brush, so it still follows a
/// theme or accent change exactly like the rest of the app.
/// </summary>
public static class ButtonLabelColour
{
    public static readonly DependencyProperty KeepProperty = DependencyProperty.RegisterAttached(
        "Keep", typeof(bool), typeof(ButtonLabelColour), new PropertyMetadata(false, OnKeepChanged));

    public static void SetKeep(DependencyObject element, bool value) => element.SetValue(KeepProperty, value);

    public static bool GetKeep(DependencyObject element) => (bool)element.GetValue(KeepProperty);

    /// <summary>A private mirror of the button's own Appearance, bound to it, so an appearance change
    /// re-applies the label colour through an ordinary property callback. A
    /// DependencyPropertyDescriptor.AddValueChanged subscription did the same, but its static tracker held
    /// a strong reference to every button (and through it the whole window) for the life of the process,
    /// since nothing ever removed it.</summary>
    private static readonly DependencyProperty TrackedAppearanceProperty = DependencyProperty.RegisterAttached(
        "TrackedAppearance", typeof(ControlAppearance), typeof(ButtonLabelColour),
        new PropertyMetadata(ControlAppearance.Primary, OnTrackedAppearanceChanged));

    private static void OnKeepChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Button button)
            return;

        // Unsubscribe first either way, so Keep toggled off and on again never attaches twice.
        button.IsEnabledChanged -= OnIsEnabledChanged;
        System.Windows.Data.BindingOperations.ClearBinding(button, TrackedAppearanceProperty);

        if (!Equals(e.NewValue, true))
            return;

        Apply(button);

        // An appearance chosen or changed after the style landed has to bring its own label colour with it.
        System.Windows.Data.BindingOperations.SetBinding(button, TrackedAppearanceProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath(Button.AppearanceProperty),
            Source = button,
            Mode = System.Windows.Data.BindingMode.OneWay,
        });

        // Same local-value-outranks-a-template-trigger reasoning as the press fix above, for a second
        // template trigger: disabling a Primary button fades its FILL to a near-white grey (WPF-UI's own
        // generic disabled brush) but this Keep mechanism was still pinning the label to
        // AccentButtonForeground, the colour picked for a saturated accent fill, not a pale disabled one,
        // measured at about 1.1:1 against it. IsEnabled changing needs the same re-apply the appearance does.
        // An instance event on the button itself, so it holds no reference beyond the button's own lifetime.
        button.IsEnabledChanged += OnIsEnabledChanged;
    }

    private static void OnTrackedAppearanceChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is Button button && GetKeep(button))
            Apply(button);
    }

    private static void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Button button)
            Apply(button);
    }

    private static void Apply(Button button) =>
        button.SetResourceReference(
            TextElement.ForegroundProperty,
            button.Appearance == ControlAppearance.Primary && button.IsEnabled
                ? "AccentButtonForeground"
                : "TextFillColorPrimaryBrush");
}
