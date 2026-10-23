namespace UltimateUpdater.Services;

/// <summary>
/// A side-channel witness for whether <see cref="AppPaths"/> has been
/// initialized in this process, kept deliberately apart from AppPaths itself. A flag ON AppPaths could
/// never prove "never touched": reading ANY static member of a type runs that type's own static
/// constructor first if it has not run yet, so the read itself would always force the answer to "touched",
/// regardless of what happened before it. This type has no other relationship to AppPaths beyond the one
/// call AppPaths' static constructor makes into it, so reading <see cref="WasTouched"/> never triggers
/// AppPaths' own initializer.
/// </summary>
internal static class AppPathsTouchTracker
{
    internal static bool WasTouched { get; private set; }

    internal static void MarkTouched() => WasTouched = true;
}
