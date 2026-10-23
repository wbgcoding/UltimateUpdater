// WinForms is referenced solely for the tray NotifyIcon, which WPF has no equivalent for.
// That drags in WinForms types that collide with WPF's by name, so the WPF ones win project-wide;
// the few WinForms types actually used are fully qualified at their use site.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
