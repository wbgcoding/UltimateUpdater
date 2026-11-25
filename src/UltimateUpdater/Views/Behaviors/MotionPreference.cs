using System.Windows;

namespace UltimateUpdater.Views.Behaviors;

/// <summary>Whether decorative animations may run: the Windows "Show animations" setting.</summary>
internal static class MotionPreference
{
    /// <summary>Lets the animation tests run on a machine with animations turned off, such as a build
    /// server. Null (the default) reads the real setting.</summary>
    internal static bool? AnimationsEnabledForTest { get; set; }

    /// <summary>Read each time by code that asks. A XAML <c>{x:Static}</c> binding to it is read once, so a trigger that uses it follows the Windows setting from the next start of the app.</summary>
    public static bool AnimationsEnabled => AnimationsEnabledForTest ?? SystemParameters.ClientAreaAnimation;
}
