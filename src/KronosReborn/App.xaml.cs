using System;
using System.Windows;

namespace KronosReborn;

/// <summary>
/// Application entry point.
/// </summary>
/// <remarks>
/// Scaffolding only. There is no startup work, no single-instance handling, no database and no
/// unhandled-exception reporting yet, because none of it is needed until there is behaviour to
/// protect.
///
/// When those do arrive, the two that matter most on Windows are worth deciding deliberately rather
/// than by default: a second-instance check, because two copies racing to write the same database
/// causes corruption rather than a visible error, and a last-chance exception handler, because an
/// unhandled exception on the UI thread otherwise closes the window with nothing recorded.
/// </remarks>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
    }
}
