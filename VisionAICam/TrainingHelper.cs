using System.Diagnostics;
using System.IO;
using System.Windows;
using VisionAICam.Pages;

public static void LaunchYOLOv8Training(
    List<TrainingOption> datasetOptions,
    List<TrainingOption> modelOptions,
    List<TrainingOption> trainingOptions)
{
    // Validate dataset root
    string datasetRoot = GetOptionValue(datasetOptions, "Path");
    if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
    {
        ShowError("❌ Dataset path is invalid or missing.", "Training aborted due to invalid dataset path.");
        return;
    }

    // Validate data.yaml
    string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
    if (!File.Exists(datasetYamlPath))
    {
        ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.");
        return;
    }

    // Fix relative paths inside data.yaml
    FixDataYamlPaths(datasetYamlPath, datasetRoot);

    // Extract training parameters
    string backbone = GetOptionValue(modelOptions, "Backbone") ?? "yolov8n.pt";
    string weights = GetOptionValue(modelOptions, "Pretrained Weights") ?? "COCO";
    string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
    string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
    string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
    string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "Adam";
    string scheduler = GetOptionValue(trainingOptions, "Scheduler") ?? "StepLR";

    // Resolve directories
    string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    string scriptPath = Path.Combine(baseDirectory, "Script", "train_yolov8.py");
    string expName = $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
    string trainingOutputDir = Path.Combine(baseDirectory, "training_output");
    string modelSaveDir = Path.Combine(trainingOutputDir, "models");
    string trainingLogPath = Path.Combine(trainingOutputDir, "log");
    string resultsDir = Path.Combine(modelSaveDir, expName); // NEW: results directory
    string pretrainFolderPath = Path.Combine(baseDirectory, "pretrain");

    if (!File.Exists(scriptPath))
    {
        ShowError("❌ Python training script not found.", $"Expected at:\n{scriptPath}");
        return;
    }

    EnsureDirectory(trainingOutputDir, "Training Output");
    EnsureDirectory(modelSaveDir, "Model Save");
    EnsureDirectory(trainingLogPath, "Training Log");
    EnsureDirectory(resultsDir, "Results Output"); // NEW: ensure results directory

    // Build argument list safely
    var argsList = new List<string>
    {
        $"--input \"{datasetYamlPath}\"",
        $"--backbone \"{backbone}\"",
        $"--weights \"{weights}\"",
        $"--epochs \"{epochs}\"",
        $"--batch \"{batchSize}\"",
        $"--lr \"{lr}\"",
        $"--opt \"{optimizer}\"",
        $"--sched \"{scheduler}\"",
        $"--modelSaveDir \"{modelSaveDir}\"",
        $"--Log_dir \"{trainingLogPath}\"",
        $"--name \"{expName}\"",
        $"--base_dir \"{baseDirectory}\"",
        $"--pretrain_dir \"{pretrainFolderPath}\"",
        $"--results_dir \"{resultsDir}\"" // NEW: pass results directory
    };

    string args = string.Join(" ", argsList);

    // Resolve Python path
    string pythonPath;
    try
    {
        pythonPath = ResolvePythonPath();
    }
    catch (FileNotFoundException ex)
    {
        MessageBox.Show(ex.Message, "Python Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
        return;
    }

    // Build full command with proper quoting
    string fullCommand = $"\"{pythonPath}\" \"{scriptPath}\" {args}";

    // Launch in new console window
    var startInfo = new ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = $"/k \"{fullCommand}\"",
        UseShellExecute = true,
        CreateNoWindow = false
    };

    try
    {
        Process.Start(startInfo);
    }
    catch (Exception ex)
    {
        MessageBox.Show($"❌ Failed to launch training script.\n{ex.Message}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}