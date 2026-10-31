using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Prism.Desktop.Views;

public partial class BlueprintGraphPageView : UserControl
{
    public BlueprintGraphPageView()
    {
        InitializeComponent();
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => GraphCanvas.FitToGraph(wholeGraph: true);

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => GraphCanvas.ZoomBy(1.25);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => GraphCanvas.ZoomBy(0.8);
}
