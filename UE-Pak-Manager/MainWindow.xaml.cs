using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using UAssetAPI;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace UE_Pak_Manager;

public partial class MainWindow : Window
{
    private readonly PakManagerService _pakService = new();
    private readonly ObservableCollection<PakFileItem> _pakFiles = [];
    private readonly ObservableCollection<DirectoryNode> _packDirectories = [];
    private readonly ObservableCollection<PackFilePreviewItem> _packPreviewFiles = [];
    private readonly ICollectionView _pakFilesView;

    private string? _packSourceRoot;
    private DirectoryNode? _selectedPackDirectory;

    public MainWindow()
    {
        InitializeComponent();
        PakFilesGrid.ItemsSource = _pakFiles;
        PackDirectoryTree.ItemsSource = _packDirectories;
        PackFilesGrid.ItemsSource = _packPreviewFiles;

        _pakFilesView = CollectionViewSource.GetDefaultView(_pakFiles);
        _pakFilesView.Filter = FilterPakFile;

        PakVersionComboBox.ItemsSource = Enum.GetValues<PakVersion>();
        PakVersionComboBox.SelectedItem = PakVersion.V11;
        CompressionComboBox.ItemsSource = Enum.GetValues<PakCompression>();
        CompressionComboBox.SelectedItem = PakCompression.Zlib;
    }

    protected override void OnClosed(EventArgs e)
    {
        _pakService.Dispose();
        base.OnClosed(e);
    }

    private async void OpenPakButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Filter = "Unreal Pak (*.pak)|*.pak|所有文件 (*.*)|*.*",
            Title = "打开 Pak 文件",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunBusyAsync("正在打开 Pak...", () =>
        {
            IReadOnlyList<PakFileItem> files = _pakService.Open(dialog.FileName);
            Dispatcher.Invoke(() =>
            {
                _pakFiles.Clear();
                foreach (PakFileItem file in files)
                {
                    _pakFiles.Add(file);
                }

                OpenPakPathTextBox.Text = dialog.FileName;
                PakInfoText.Text = $"{files.Count} 个文件 | 版本 {_pakService.Version} | 挂载点 {_pakService.MountPoint}";
            });
        }, "Pak 已打开");
    }

    private async void ExtractSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        PakFileItem[] selected = PakFilesGrid.SelectedItems.Cast<PakFileItem>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "请先选择一个或多个文件。", "解包", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExtractAsync(selected);
    }

    private async void ExtractAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pakFiles.Count == 0)
        {
            MessageBox.Show(this, "请先打开一个 Pak 文件。", "解包", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExtractAsync(_pakFiles.ToArray());
    }

    private async Task ExtractAsync(IReadOnlyList<PakFileItem> files)
    {
        string? outputDirectory = PickFolder("选择解包输出目录");
        if (outputDirectory is null)
        {
            return;
        }

        Progress<string> progress = new(message => StatusText.Text = message);
        await RunBusyAsync($"正在提取 {files.Count} 个文件...", () => _pakService.Extract(files, outputDirectory, progress), "提取完成");
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _pakFilesView.Refresh();
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Clear();
    }

    private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        string? directory = PickFolder("选择要打包的源目录");
        if (directory is null)
        {
            return;
        }

        LoadPackSourceDirectory(directory);
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Filter = "Unreal Pak (*.pak)|*.pak|所有文件 (*.*)|*.*",
            Title = "保存 Pak 文件",
            DefaultExt = ".pak",
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputPakTextBox.Text = dialog.FileName;
        }
    }

    private async void PackButton_Click(object sender, RoutedEventArgs e)
    {
        string sourceDirectory = SourceDirectoryTextBox.Text.Trim();
        string outputPak = OutputPakTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(outputPak))
        {
            MessageBox.Show(this, "请先选择源目录和输出 Pak 路径。", "打包", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PakVersion version = (PakVersion)(PakVersionComboBox.SelectedItem ?? PakVersion.V11);
        PakCompression? compression = UseCompressionCheckBox.IsChecked == true
            ? (PakCompression)(CompressionComboBox.SelectedItem ?? PakCompression.Zlib)
            : null;
        Progress<string> progress = new(message => StatusText.Text = message);
        int packedCount = 0;

        await RunBusyAsync("正在打包 Pak...", () =>
        {
            packedCount = PakManagerService.PackDirectory(
                sourceDirectory,
                outputPak,
                MountPointTextBox.Text,
                PakPathPrefixTextBox.Text,
                version,
                compression,
                progress);
        }, () => $"打包完成：{packedCount} 个文件");
    }

    private void PackDirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedPackDirectory = e.NewValue as DirectoryNode;
        RefreshPackPreview();
    }

    private void PakPathPrefixTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshPackPreview();
    }

    private void LoadPackSourceDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        _packSourceRoot = fullPath;
        SourceDirectoryTextBox.Text = fullPath;
        if (string.IsNullOrWhiteSpace(OutputPakTextBox.Text))
        {
            OutputPakTextBox.Text = Path.Combine(Path.GetDirectoryName(fullPath) ?? fullPath, Path.GetFileName(fullPath) + ".pak");
        }

        DirectoryNode root = BuildDirectoryNode(fullPath, isRoot: true);
        _packDirectories.Clear();
        _packDirectories.Add(root);
        _selectedPackDirectory = root;
        RefreshPackPreview();
    }

    private DirectoryNode BuildDirectoryNode(string directory, bool isRoot = false)
    {
        DirectoryNode node = new()
        {
            Name = isRoot ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : Path.GetFileName(directory),
            FullPath = directory,
        };

        foreach (string child in Directory.GetDirectories(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            node.Children.Add(BuildDirectoryNode(child));
        }

        return node;
    }

    private void RefreshPackPreview()
    {
        if (_packSourceRoot is null || _selectedPackDirectory is null || PackFilesGrid is null)
        {
            return;
        }

        _packPreviewFiles.Clear();
        string prefix = PakPathPrefixTextBox.Text;
        string[] files = Directory.GetFiles(_selectedPackDirectory.FullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string file in files)
        {
            string relativePath = Path.GetRelativePath(_packSourceRoot, file).Replace('\\', '/');
            _packPreviewFiles.Add(new PackFilePreviewItem
            {
                Name = Path.GetFileName(file),
                RelativePath = relativePath,
                PakPath = BuildPreviewPakPath(relativePath, prefix),
                Size = new FileInfo(file).Length,
            });
        }

        string displayPath = Path.GetRelativePath(_packSourceRoot, _selectedPackDirectory.FullPath);
        if (displayPath == ".")
        {
            displayPath = Path.GetFileName(_packSourceRoot);
        }

        PackCurrentDirectoryText.Text = displayPath.Replace('\\', '/');
        PackFileCountText.Text = $"{files.Length} 个文件";
        int totalFiles = Directory.GetFiles(_packSourceRoot, "*", SearchOption.AllDirectories).Length;
        PackSummaryText.Text = $"将打包 {totalFiles} 个文件";
    }

    private static string BuildPreviewPakPath(string relativePath, string pakPathPrefix)
    {
        string prefix = pakPathPrefix.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(prefix) ? relativePath : $"{prefix}/{relativePath}";
    }

    private bool FilterPakFile(object item)
    {
        if (item is not PakFileItem file)
        {
            return false;
        }

        string query = SearchTextBox.Text.Trim();
        return query.Length == 0 || file.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunBusyAsync(string busyMessage, Action action, string doneMessage)
    {
        await RunBusyAsync(busyMessage, action, () => doneMessage);
    }

    private async Task RunBusyAsync(string busyMessage, Action action, Func<string> doneMessage)
    {
        SetBusy(true, busyMessage);
        try
        {
            await Task.Run(action);
            StatusText.Text = doneMessage();
        }
        catch (Exception ex)
        {
            StatusText.Text = "失败";
            MessageBox.Show(this, ex.Message, "UE Pak 管理器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private void SetBusy(bool isBusy, string message)
    {
        BusyProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message;
        IsEnabled = !isBusy;
    }

    private static string? PickFolder(string description)
    {
        OpenFolderDialog dialog = new()
        {
            Title = description,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
