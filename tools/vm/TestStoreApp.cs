using System;
using System.Drawing;
using System.Windows.Forms;

// A packaged test app for the VM: one window that shows its own version, so a launch and an update are visible.
internal static class TestStoreApp
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        var version = typeof(TestStoreApp).Assembly.GetName().Version.ToString();
        var form = new Form
        {
            Text = "Test Store App " + version,
            Width = 420,
            Height = 160,
            StartPosition = FormStartPosition.CenterScreen,
        };
        form.Controls.Add(new Label
        {
            Name = "lblVersion",
            Text = "Test Store App " + version,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 14f),
        });
        Application.Run(form);
    }
}
