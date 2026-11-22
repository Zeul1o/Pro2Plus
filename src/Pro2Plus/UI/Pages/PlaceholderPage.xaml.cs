using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Pro2Plus.UI.Pages;

public partial class PlaceholderPage : UserControl
{
    public PlaceholderPage(string headline, string detail)
    {
        InitializeComponent();
        HeadlineText.Text = headline;
        DetailText.Text = detail;
    }
}
