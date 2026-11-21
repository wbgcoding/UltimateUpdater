// WinForms is referenced only for the tray NotifyIcon. Its types collide with WPF's by name, so the WPF ones win project-wide and the few WinForms types used are fully qualified.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
