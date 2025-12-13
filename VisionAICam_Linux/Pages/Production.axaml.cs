using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VisionAICam_Linux;

public partial class Production : UserControl
{
    public Production()
    {
        InitializeComponent();
    }

    // Provide the missing InitializeComponent implementation for Avalonia XAML loading
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}