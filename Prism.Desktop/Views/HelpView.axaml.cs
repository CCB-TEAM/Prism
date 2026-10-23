using Avalonia.Controls;

namespace Prism.Desktop.Views;

/// <summary>
/// 使用说明页。版式与内容块的模板全在 XAML 里，这里只负责加载：
/// 文案在 <see cref="Docs.PrismDocs"/>，截图在 <see cref="Docs.DocImageProvider"/>。
/// </summary>
public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
    }
}
