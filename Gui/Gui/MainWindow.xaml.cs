using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Gui
{
    /// <summary>
    /// Hosts a single terminal session.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            Terminal.TitleChanged += title => Title = title;
            Closed += (_, _) => Terminal.Session.Dispose();

            AppWindow.Resize(new SizeInt32(1000, 640));
        }
    }
}
