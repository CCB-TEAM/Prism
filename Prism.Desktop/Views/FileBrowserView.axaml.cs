using Avalonia.Controls;
using Avalonia.Input;
using Prism.Desktop.ViewModels;

namespace Prism.Desktop.Views;

public partial class FileBrowserView : UserControl
{
    public FileBrowserView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 单击激活（与 Web 版一致，兼容手机触摸）：目录进入，文件预览。
    /// </summary>
    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedItem is not null)
        {
            vm.ActivateCommand.Execute(null);
        }
    }
}
