using System.Windows;

namespace UltimateUpdater.Views;

/// <summary>Whether decorative animations may run: the Windows "Show animations" setting.</summary>
internal static class MotionPreference
{
    /// <summary>Lets the animation tests run on a machine with animations turned off, such as a build
    /// server. Null (the default) reads the real setting.</summary>
    internal static bool? AnimationsEnabledForTest { get; set; }

    public static bool AnimationsEnabled => AnimationsEnabledForTest ?? SystemParameters.ClientAreaAnimation;
}
