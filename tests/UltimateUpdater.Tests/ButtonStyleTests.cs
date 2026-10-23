using System.Xml.Linq;

namespace UltimateUpdater.Tests;

/// <summary>
/// Pressing a button must never turn its label or its icon black.
/// The cause is in WPF-UI: <c>ui:Button</c> declares its own <c>PressedForeground</c> property, defaults it
/// to <c>SystemColors.ControlTextBrush</c>, plain black, and never sets it anywhere in the shipped style.
/// The pressed trigger then writes that black onto the button's Foreground, which the label AND the icon
/// inherit, in every theme and for every appearance.
/// Asserted against App.xaml rather than against a live control on purpose. The resolved brush depends on
/// which theme happens to be applied in the shared test Application, and a value read that way passed alone
/// and failed in the full run, the contract that actually matters is "the property is set, and the style is
/// derived correctly", and that lives in the file.
/// </summary>
public class ButtonStyleTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>Walks up from the test binaries to the source tree, like XamlBindingTests does.</summary>
    private static XElement AppXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "App.xaml");
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate).Root
                    ?? throw new InvalidOperationException("App.xaml has no root element.");
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate App.xaml from the test output path.");
    }

    private static IEnumerable<XElement> Styles() =>
        AppXaml().Descendants(Presentation + "Style");

    [Fact]
    public void The_app_sets_a_pressed_foreground_for_every_button()
    {
        var buttonStyle = Styles().FirstOrDefault(s => (string?)s.Attribute("TargetType") == "{x:Type ui:Button}");

        Assert.NotNull(buttonStyle);

        var setters = buttonStyle!.Descendants(Presentation + "Setter")
            .Select(s => (string?)s.Attribute("Property"))
            .ToList();

        Assert.Contains("PressedForeground", setters);
    }

    /// <summary>
    /// A style must NEVER derive from the implicit style of its own target type: WPF resolves that
    /// self-reference to nothing and the control loses its whole template, every button in the app fell
    /// back to the plain WPF one, with no icon and no accent fill. WPF-UI keys its base as
    /// <c>DefaultUiButtonStyle</c>, and that is what the derived style has to name.
    /// </summary>
    [Fact]
    public void No_style_derives_from_the_implicit_style_of_its_own_type()
    {
        foreach (var style in Styles())
        {
            var target = (string?)style.Attribute("TargetType");
            var basedOn = (string?)style.Attribute("BasedOn");

            if (target is null || basedOn is null)
                continue;

            Assert.False(
                basedOn == $"{{StaticResource {target}}}",
                $"The implicit style for {target} derives from itself, which strips the control's template.");
        }
    }

    /// <summary>The per-row update button is neutral: the one accent-filled install action on screen is the
    /// footer's "update selection" button, a blue button on every row drowned it out.</summary>
    [Fact]
    public void The_row_update_button_is_secondary_not_accent_filled()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? xamlPath = null;
        while (directory is not null && xamlPath is null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views", "MainWindow.xaml");
            if (File.Exists(candidate))
                xamlPath = candidate;
            directory = directory.Parent;
        }

        Assert.NotNull(xamlPath);
        var rowButton = XDocument.Load(xamlPath!).Descendants()
            .Single(e => e.Name.LocalName == "Button"
                         && (string?)e.Attribute("Command") == "{Binding InstallCommand}");

        Assert.Equal("Secondary", (string?)rowButton.Attribute("Appearance"));
    }

    /// <summary>The accent-filled case gets the accent button's own label brush rather than the base one.</summary>
    [Fact]
    public void An_accent_button_keeps_an_on_accent_label_while_pressed()
    {
        var trigger = AppXaml().Descendants(Presentation + "Trigger")
            .FirstOrDefault(t => (string?)t.Attribute("Property") == "Appearance"
                                 && (string?)t.Attribute("Value") == "Primary");

        Assert.NotNull(trigger);

        var setter = trigger!.Elements(Presentation + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("Property") == "PressedForeground");

        Assert.NotNull(setter);
        Assert.Equal("{DynamicResource AccentButtonForeground}", (string?)setter!.Attribute("Value"));
    }
}
