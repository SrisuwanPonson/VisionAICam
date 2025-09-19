using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
            if (item is not TrainingOption option)
                return null;

            string name = option.Name?.Trim();

            // Force TextBlock for "Architecture"
            if (name.Equals("Architecture", StringComparison.OrdinalIgnoreCase))
                return CreateTextBlockTemplate();

            // Use ComboBox if choices exist
            if (_optionSources.TryGetValue(name, out var choices))
                return CreateComboBoxTemplate(choices);

            // Default to TextBlock
            return CreateTextBlockTemplate();
        }

        private DataTemplate CreateComboBoxTemplate(string[] choices)
        {
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(ComboBox));

            factory.SetBinding(ComboBox.SelectedItemProperty, new Binding("Value") { Mode = BindingMode.TwoWay });
            factory.SetValue(ComboBox.ItemsSourceProperty, choices);
            factory.SetValue(ComboBox.MarginProperty, new Thickness(2, 0, 2, 0));
            factory.SetValue(ComboBox.ToolTipProperty, "Select a value");

            // Attach selection changed event
            factory.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(OnComboBoxChanged));

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

        // Handle ComboBox selection changes
        private void OnComboBoxChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is TrainingOption option)
            {
                // Ensure this is not the first time loading
                if (e.RemovedItems.Count == 0)
                    return;

                // Update the bound value
                option.Value = combo.SelectedItem?.ToString() ?? string.Empty;

                // Log the change
                Console.WriteLine($"📌 Option changed: {option.Name} → {option.Value}");

                // Fire signal if "Training Mode" was changed
                if (option.Name.Equals("Training Mode", StringComparison.OrdinalIgnoreCase))
                {
                    GlobalSignals.TrainingModeChanged.Fire(option.Value);
                }
            }
        }






    }
}