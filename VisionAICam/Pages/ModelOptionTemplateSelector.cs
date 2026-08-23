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

        // ✅ Tooltip dictionary
        private static readonly Dictionary<string, string> Tooltips = new()
        {
            { "Architecture", "🏗️ Model architecture (YOLOv8, Faster R-CNN, etc.)" },
            { "Variant", "📏 Model size:\n• n (nano): Fastest, smallest\n• s (small): Balanced\n• m (medium): Good accuracy\n• l (large): High accuracy\n• x (xlarge): Best accuracy" },
            { "Input Size", "🖼️ Input resolution (auto-detected)\nHigher = better accuracy, slower" },
            { "Backbone", "🦴 Feature extraction network" },
            { "Pretrained Weights", "⚖️ Starting weights:\n• Pretrained: Recommended\n• None: From scratch" },
            { "Training mode", "🎯 Strategy:\n• scratch: From zero (1000+ images)\n• topup: Fine-tune (100+ images)\n• benchmark: Quick test" },
            { "📊 Dataset Size", "📈 Total images in dataset" },
            { "🤖 Auto Variant", "🔮 Auto-selected based on dataset size" },
            { "Epochs", "🔄 Training iterations (50-300)" },
            { "Batch Size", "📦 Images per batch (8-32)" },
            { "Learning Rate", "📚 Update step size (0.0001-0.001)" },
            { "Optimizer", "⚙️ Update algorithm:\n• Adam: Best default\n• SGD: Classic\n• AdamW: Adam + regularization" },
            { "Scheduler", "📊 LR adjustment:\n• None: Fixed\n• StepLR: Step decrease\n• CosineAnnealing: Smooth" },
            { "Momentum", "🎯 Optimization momentum (0.9)" },
            { "Weight Decay", "🏋️ Regularization (0.0001-0.001)" }
        };

        public ModelOptionTemplateSelector(Dictionary<string, string[]> optionSources)
        {
            _optionSources = optionSources;
        }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is not TrainingOption option)
                return null;

            string name = option.Name?.Trim();

            // Get tooltip for this option
            string tooltip = Tooltips.TryGetValue(name, out var tip) ? tip : name;

            // Force TextBlock for "Architecture" and read-only fields
            if (name.Equals("Architecture", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("📊") || name.StartsWith("🤖"))
                return CreateTextBlockTemplate(tooltip);

            // Use ComboBox if choices exist
            if (_optionSources.TryGetValue(name, out var choices))
                return CreateComboBoxTemplate(choices, tooltip);

            // Default to TextBlock
            return CreateTextBlockTemplate(tooltip);
        }

        private DataTemplate CreateComboBoxTemplate(string[] choices, string tooltip)
        {
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(ComboBox));

            factory.SetBinding(ComboBox.SelectedItemProperty, new Binding("Value") { Mode = BindingMode.TwoWay });
            factory.SetValue(ComboBox.ItemsSourceProperty, choices);
            factory.SetValue(ComboBox.MarginProperty, new Thickness(2, 0, 2, 0));
            factory.SetValue(ComboBox.ToolTipProperty, tooltip); // ✅ Add tooltip

            factory.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(OnComboBoxChanged));

            template.VisualTree = factory;
            return template;
        }

        private DataTemplate CreateTextBlockTemplate(string tooltip)
        {
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));

            factory.SetBinding(TextBlock.TextProperty, new Binding("Value"));
            factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            factory.SetValue(TextBlock.MarginProperty, new Thickness(2, 0, 2, 0));
            factory.SetValue(TextBlock.ToolTipProperty, tooltip); // ✅ Add tooltip

            template.VisualTree = factory;
            return template;
        }

        private void OnComboBoxChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is TrainingOption option)
            {
                if (e.RemovedItems.Count == 0)
                    return;

                option.Value = combo.SelectedItem?.ToString() ?? string.Empty;
                Console.WriteLine($"📌 Option changed: {option.Name} → {option.Value}");

                if (option.Name.Equals("Training Mode", StringComparison.OrdinalIgnoreCase))
                {
                    GlobalSignals.TrainingModeChanged.Fire(option.Value);
                }
            }
        }
    }
}