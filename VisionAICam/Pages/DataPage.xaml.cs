using System;
using System.Windows;
using System.Windows.Controls;
using VisionAICam.Core; // ensure MasterController reference

namespace VisionAICam.Pages
{
    /// <summary>
    /// Interaction logic for DataPage.xaml
    /// </summary>
    public partial class DataPage : Page
    {
        public DataPage()
        {
            InitializeComponent();

            // Ensure handlers are attached even if XAML didn't wire them
            this.Loaded -= Page_Loaded;
            this.Loaded += Page_Loaded;
            this.Unloaded -= Page_Unloaded;
            this.Unloaded += Page_Unloaded;
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: implement filter
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: implement export
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: implement delete
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (this.FindName("ResultsGrid") is DataGrid dg)
                {
                    // bind directly to centrally managed collection (no duplicate binding)
                    var shared = MasterController.Instance.SharedResults;
                    if (!ReferenceEquals(dg.ItemsSource, shared))
                    {
                        dg.ItemsSource = shared;
                    }
                }
            }
            catch (Exception)
            {
                // swallow to avoid breaking UI load; logging can be added if needed
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (this.FindName("ResultsGrid") is DataGrid dg)
                {
                    // detach ItemsSource to avoid holding references after page unload
                    if (dg.ItemsSource != null)
                        dg.ItemsSource = null;
                }
            }
            catch { }
        }
    }
}
