using System.Windows;
using System.Windows.Documents;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace UltimateUpdater.Views.Behaviors;

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

    /// <summary>A private mirror of the button's own Appearance, bound to it, so an appearance change re-applies
    /// the label colour through an ordinary property callback. (A DependencyPropertyDescriptor subscription would
    /// leak every button through its static tracker.)</summary>
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

        // Same local-value reasoning for a second template trigger: disabling a Primary button fades its fill to a
        // near-white grey, and AccentButtonForeground (picked for a saturated fill) would read at about 1.1:1 on it.
        // IsEnabled changing needs the same re-apply. An instance event, so it holds no reference beyond the button.
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
