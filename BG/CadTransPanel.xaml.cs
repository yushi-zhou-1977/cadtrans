using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>
    /// CadTransPanel.xaml 的交互逻辑
    /// 仅负责初始化，所有业务逻辑由 CadTransPanelViewModel 处理
    /// </summary>
    public partial class CadTransPanel : UserControl
    {
        public CadTransPanel()
        {
            InitializeComponent();
            DataContext = new CadTransPanelViewModel();
        }
    }
}
