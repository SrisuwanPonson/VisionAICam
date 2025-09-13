using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;

namespace VisionAICam.Pages
{
    public class TrainingOption
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }
    public class ModelOptionTemplateSelector : DataTemplateSelector
    {
        private readonly Dictionary<string, string[]> _optionSources;

        public ModelOptionTemplateSelector(Dictionary<string, string[]> optionSources)
        {
            _optionSources = optionSources;
        }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            var option = item as TrainingOption;
            if (option == null)
                return null;

            // Force TextBlock for "architecture"
            if (option.Name.Equals("architecture", StringComparison.OrdinalIgnoreCase))
            {
                return CreateTextBlockTemplate();
            }

            // Use ComboBox if choices exist
            if (_optionSources.TryGetValue(option.Name, out var choices))
            {
                return CreateComboBoxTemplate(choices);
            }

            // Default to TextBlock
            return CreateTextBlockTemplate();
        }

        private DataTemplate CreateComboBoxTemplate(string[] choices)
        {
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(ComboBox));
            factory.SetBinding(ComboBox.SelectedItemProperty, new Binding("Value"));
            factory.SetValue(ComboBox.ItemsSourceProperty, choices);
            factory.SetValue(ComboBox.MarginProperty, new Thickness(2, 0, 2, 0));
            factory.SetValue(ComboBox.ToolTipProperty, "Select a value");
            template.VisualTree = factory;
            return template;
        }

        private DataTemplate CreateTextBlockTemplate()
        {
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding("Value"));
            factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            factory.SetValue(TextBlock.MarginProperty, new Thickness(2, 0, 2, 0));
            factory.SetValue(TextBlock.ToolTipProperty, "Fixed value");
            template.VisualTree = factory;
            return template;
        }

        private void CreateAppFolder(string folderName)
        {
            string appFolderPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folderName);
            if (!Directory.Exists(appFolderPath))
            {
                Directory.CreateDirectory(appFolderPath);
            }
        }


    }
}
