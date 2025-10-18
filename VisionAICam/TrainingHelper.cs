using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using VisionAICam.Pages;

namespace VisionAICam.Utilities
{
    public static class TrainingHelper
    {
        internal static bool LaunchYOLOv8Training(
    List<TrainingOption> datasetOptions,
    List<TrainingOption> modelOptions,
    List<TrainingOption> trainingOptions,
    Action<string> updateStatus = null)
        {
            updateStatus?.Invoke("🔍 Validating training configuration...");

            string datasetRoot = GetOptionValue(datasetOptions, "Path");
            if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
            {
                ShowError("❌ Dataset path is invalid or missing.", "Training aborted due to invalid dataset path.", updateStatus);
                return false;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return false;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputWidth = "640";
            string inputHeight = "480";
            string backbone = GetOptionValue(modelOptions, "Backbone") ?? "yolov8n";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "Adam";
            string scheduler = GetOptionValue(trainingOptions, "Scheduler") ?? "StepLR";

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string scriptPath = Path.Combine(baseDirectory, "Script", "train_yolov8.py");
            string trainingOutputDir = Path.Combine(baseDirectory, "training_output");
            string modelSaveDir = Path.Combine(trainingOutputDir, "models");
            string trainingLogPath = Path.Combine(trainingOutputDir, "log");
            string resultsDir = Path.Combine(modelSaveDir, expName);
            string pretrainFolderPath = Path.Combine(baseDirectory, "pretrain");

            if (!File.Exists(scriptPath))
            {
                ShowError("❌ Python training script not found.", $"Expected at:\n{scriptPath}", updateStatus);
                return false;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            var argsList = new List<string>
    {
        $"--data \"{datasetYamlPath}\"",
        $"--imgsz {inputWidth} {inputHeight}",
        $"--epochs \"{epochs}\"",
        $"--batch \"{batchSize}\"",
        $"--lr \"{lr}\"",
        $"--opt \"{optimizer}\"",
        $"--modelSaveDir \"{modelSaveDir}\"",
        $"--Log_dir \"{trainingLogPath}\"",
        $"--name \"{expName}\"",
        $"--base_dir \"{baseDirectory}\"",
        $"--results_dir \"{resultsDir}\"",
        $"--mode \"{trainingMode}\""
    };

            switch (trainingMode)
            {
                case "scratch":
                    string cfgPath = Path.Combine(pretrainFolderPath, "yolov8.yaml");
                    string modelScale = GetOptionValue(modelOptions, "Variant")?.ToLower() ?? "n";
                    argsList.Add($"--cfg \"{cfgPath}\"");
                    argsList.Add($"--model \"{modelScale}\"");
                    updateStatus?.Invoke($"🧼 Scratch mode selected. Using scale: {modelScale}");
                    break;

                case "topup":
                case "benchmark":
                    if (string.IsNullOrWhiteSpace(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights not specified.", $"Training aborted for mode: {trainingMode}.", updateStatus);
                        return false;
                    }

                    string weightsPath = pretrainedWeights.EndsWith(".pt") ? pretrainedWeights : pretrainedWeights + ".pt";
                    if (!File.Exists(weightsPath))
                    {
                        ShowError("❌ Pretrained weights file not found.", $"Expected at:\n{weightsPath}", updateStatus);
                        return false;
                    }

                    argsList.Add($"--weights \"{weightsPath}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return false;
            }

            string args = string.Join(" ", argsList);

            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return false;
            }

            string fullCommand = $"\"{pythonPath}\" \"{scriptPath}\" {args}";
            updateStatus?.Invoke("🚀 Launching training script...");

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
                return true;
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch training script.\n{ex.Message}", "Launch Error", updateStatus);
                return false;
            }
        }


        internal static bool LaunchYOLOv5Training(
    List<TrainingOption> datasetOptions,
    List<TrainingOption> modelOptions,
    List<TrainingOption> trainingOptions,
    Action<string> updateStatus = null)
        {
            updateStatus?.Invoke("🔍 Validating training configuration...");

            string datasetRoot = GetOptionValue(datasetOptions, "Path");
            if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
            {
                ShowError("❌ Dataset path is invalid or missing.", "Training aborted due to invalid dataset path.", updateStatus);
                return false;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return false;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputWidth = "640";
            string inputHeight = "480";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "SGD";

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string scriptPath = Path.Combine(baseDirectory, "Script", "train_yolov5.py");
            string trainingOutputDir = Path.Combine(baseDirectory, "training_output");
            string modelSaveDir = Path.Combine(trainingOutputDir, "models");
            string trainingLogPath = Path.Combine(trainingOutputDir, "log");
            string resultsDir = Path.Combine(modelSaveDir, expName);
            string pretrainFolderPath = Path.Combine(baseDirectory, "pretrain");

            if (!File.Exists(scriptPath))
            {
                ShowError("❌ Python training script not found.", $"Expected at:\n{scriptPath}", updateStatus);
                return false;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            var argsList = new List<string>
    {
        $"--data \"{datasetYamlPath}\"",
        $"--imgsz {inputWidth} {inputHeight}",
        $"--epochs \"{epochs}\"",
        $"--batch \"{batchSize}\"",
        $"--lr \"{lr}\"",
        $"--opt \"{optimizer}\"",
        $"--modelSaveDir \"{modelSaveDir}\"",
        $"--Log_dir \"{trainingLogPath}\"",
        $"--name \"{expName}\"",
        $"--base_dir \"{baseDirectory}\"",
        $"--results_dir \"{resultsDir}\"",
        $"--mode \"{trainingMode}\""
    };

            switch (trainingMode)
            {
                case "scratch":
                    string cfgPath = Path.Combine(pretrainFolderPath, "yolov5s.yaml");
                    if (!File.Exists(cfgPath))
                    {
                        ShowError("❌ Scratch mode requires yolov5 config file.", "Training aborted due to missing config.", updateStatus);
                        return false;
                    }
                    argsList.Add($"--cfg \"{cfgPath}\"");
                    argsList.Add("--weights \"\""); // empty weights for scratch
                    updateStatus?.Invoke("🧼 Scratch mode selected. Training from config only.");
                    break;

                case "topup":
                case "benchmark":
                    if (string.IsNullOrWhiteSpace(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights not specified.", $"Training aborted for mode: {trainingMode}.", updateStatus);
                        return false;
                    }
                    if (!File.Exists(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights file not found.", $"Expected at:\n{pretrainedWeights}", updateStatus);
                        return false;
                    }
                    argsList.Add($"--weights \"{pretrainedWeights}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return false;
            }

            string args = string.Join(" ", argsList);

            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return false;
            }

            string fullCommand = $"\"{pythonPath}\" \"{scriptPath}\" {args}";
            updateStatus?.Invoke("🚀 Launching YOLOv5 training script...");

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
                return true;
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch training script.\n{ex.Message}", "Launch Error", updateStatus);
                return false;
            }
        }

        private static void ShowError(string messageBoxText, string statusText, Action<string> updateStatus)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(messageBoxText, "Training Error", MessageBoxButton.OK, MessageBoxImage.Error);
                updateStatus?.Invoke(statusText);
            });
        }

        private static void EnsureDirectory(string path, string label)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                Console.WriteLine($"📁 Created {label} directory: {path}");
            }
            else
            {
                Console.WriteLine($"✅ {label} directory already exists: {path}");
            }
        }

        private static string GetOptionValue(List<TrainingOption> options, string name)
        {
            return options.FirstOrDefault(o => o.Name == name)?.Value ?? "";
        }

        private static void FixDataYamlPaths(string yamlPath, string datasetRoot)
        {
            var lines = File.ReadAllLines(yamlPath).ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("train:"))
                    lines[i] = $"train: {Path.Combine(datasetRoot, "train", "images").Replace("\\", "/")}";
                else if (lines[i].StartsWith("val:"))
                    lines[i] = $"val: {Path.Combine(datasetRoot, "valid", "images").Replace("\\", "/")}";
                else if (lines[i].StartsWith("test:"))
                    lines[i] = $"test: {Path.Combine(datasetRoot, "test", "images").Replace("\\", "/")}";
            }
            File.WriteAllLines(yamlPath, lines);
        }

        private static void ValidatePythonPath(string pythonPath)
        {
            if (!File.Exists(pythonPath))
                throw new FileNotFoundException($"Python executable not found at {pythonPath}");
        }

        private static string ResolvePythonPath()
        {
            string pythonPath = Environment.GetEnvironmentVariable("PYTHON_PATH") ??
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python313", "python.exe");

            ValidatePythonPath(pythonPath);
            return pythonPath;
        }

        internal static bool LaunchYOLOv8SegmentationTraining(
    List<TrainingOption> datasetOptions,
    List<TrainingOption> modelOptions,
    List<TrainingOption> trainingOptions,
    Action<string> updateStatus = null)
        {
            updateStatus?.Invoke("🔍 Validating segmentation training configuration...");

            string datasetRoot = GetOptionValue(datasetOptions, "Path");
            if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
            {
                ShowError("❌ Dataset path is invalid or missing.", "Training aborted due to invalid dataset path.", updateStatus);
                return false;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return false;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputWidth = "640";
            string inputHeight = "480";
            string backbone = GetOptionValue(modelOptions, "Backbone") ?? "yolov8n";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "Adam";
            string scheduler = GetOptionValue(trainingOptions, "Scheduler") ?? "StepLR";

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string scriptPath = Path.Combine(baseDirectory, "Script", "train_yolov8_seg.py");
            string trainingOutputDir = Path.Combine(baseDirectory, "training_output");
            string modelSaveDir = Path.Combine(trainingOutputDir, "models");
            string trainingLogPath = Path.Combine(trainingOutputDir, "log");
            string resultsDir = Path.Combine(modelSaveDir, expName);
            string pretrainFolderPath = Path.Combine(baseDirectory, "pretrain");

            if (!File.Exists(scriptPath))
            {
                ShowError("❌ Python segmentation training script not found.", $"Expected at:\n{scriptPath}", updateStatus);
                return false;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            var argsList = new List<string>
    {
        $"--data \"{datasetYamlPath}\"",
        $"--imgsz {inputWidth} {inputHeight}",
        $"--weights \"{pretrainedWeights}\"",
        $"--epochs \"{epochs}\"",
        $"--batch \"{batchSize}\"",
        $"--lr \"{lr}\"",
        $"--opt \"{optimizer}\"",
        $"--modelSaveDir \"{modelSaveDir}\"",
        $"--Log_dir \"{trainingLogPath}\"",
        $"--name \"{expName}\"",
        $"--base_dir \"{baseDirectory}\"",
        $"--results_dir \"{resultsDir}\"",
        $"--mode \"{trainingMode}\""
    };

            switch (trainingMode)
            {
                case "scratch":
                    string cfgPath = Path.Combine(pretrainFolderPath, "yolov8_seg.yaml");
                    string modelScale = GetOptionValue(modelOptions, "Variant")?.ToLower() ?? "n";
                    argsList.Add($"--cfg \"{cfgPath}\"");
                    argsList.Add($"--model \"{modelScale}\"");
                    updateStatus?.Invoke($"🧼 Scratch mode selected. Using scale: {modelScale}");
                    break;

                case "topup":
                case "benchmark":
                    if (string.IsNullOrWhiteSpace(pretrainedWeights) || !File.Exists(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights file is missing or invalid.", $"Training aborted for mode: {trainingMode}.", updateStatus);
                        return false;
                    }
                    argsList.Add($"--weights \"{pretrainedWeights}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return false;
            }

            string args = string.Join(" ", argsList);

            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return false;
            }

            string fullCommand = $"\"{pythonPath}\" \"{scriptPath}\" {args}";
            updateStatus?.Invoke("🚀 Launching segmentation training script...");

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
                return true;
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch segmentation training script.\n{ex.Message}", "Launch Error", updateStatus);
                return false;
            }
        }

        internal static bool LaunchYOLOv8OBBTraining(
    List<TrainingOption> datasetOptions,
    List<TrainingOption> modelOptions,
    List<TrainingOption> trainingOptions,
    Action<string> updateStatus = null)
        {
            updateStatus?.Invoke("🔍 Validating OBB training configuration...");

            string datasetRoot = GetOptionValue(datasetOptions, "Path");
            if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
            {
                ShowError("❌ Dataset path is invalid or missing.", "Training aborted due to invalid dataset path.", updateStatus);
                return false;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return false;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputWidth = "640";
            string inputHeight = "480";

            string backbone = GetOptionValue(modelOptions, "Backbone") ?? "yolov8n-obb";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            if (string.IsNullOrWhiteSpace(pretrainedWeights))
            {
                ShowError("❌ Pretrained weights not specified.", "Training aborted due to missing weights.", updateStatus);
                return false;
            }

            pretrainedWeights += "-obb.pt";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "Adam";
            string taskType = GetOptionValue(trainingOptions, "Task") ?? "obb"; // ✅ NEW: task type

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string scriptPath = Path.Combine(baseDirectory, "Script", "train_yolov8_obb.py");
            string trainingOutputDir = Path.Combine(baseDirectory, "training_output");
            string modelSaveDir = Path.Combine(trainingOutputDir, "models");
            string trainingLogPath = Path.Combine(trainingOutputDir, "log");
            string resultsDir = Path.Combine(modelSaveDir, expName);

            if (!File.Exists(scriptPath))
            {
                ShowError("❌ Python OBB training script not found.", $"Expected at:\n{scriptPath}", updateStatus);
                return false;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            var argsList = new List<string>
    {
        $"--data \"{datasetYamlPath}\"",
        $"--imgsz {inputWidth} {inputHeight}",
        $"--weights \"{pretrainedWeights}\"",
        $"--epochs \"{epochs}\"",
        $"--batch \"{batchSize}\"",
        $"--lr \"{lr}\"",
        $"--opt \"{optimizer}\"",
        $"--task \"{taskType}\"", // ✅ NEW: inject task type
        $"--modelSaveDir \"{modelSaveDir}\"",
        $"--Log_dir \"{trainingLogPath}\"",
        $"--name \"{expName}\"",
        $"--base_dir \"{baseDirectory}\"",
        $"--results_dir \"{resultsDir}\""
    };

            updateStatus?.Invoke("🔁 Topup mode selected. Using pretrained weights...");

            string args = string.Join(" ", argsList);

            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return false;
            }

            string fullCommand = $"\"{pythonPath}\" \"{scriptPath}\" {args}";
            updateStatus?.Invoke("🚀 Launching OBB training script...");

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
                return true;
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch OBB training script.\n{ex.Message}", "Launch Error", updateStatus);
                return false;
            }
        }

        internal static bool LaunchYOLOv5OBBTraining(
    List<TrainingOption> datasetOptions,
    List<TrainingOption> modelOptions,
    List<TrainingOption> trainingOptions,
    Action<string> updateStatus = null)
        {
            updateStatus?.Invoke("🔍 Validating YOLOv5 OBB training configuration...");

            string datasetRoot = GetOptionValue(datasetOptions, "Path");
            if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
            {
                ShowError("❌ Dataset path is invalid or missing.", "Training aborted due to invalid dataset path.", updateStatus);
                return false;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return false;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputWidth = "640";
            string inputHeight = "480";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "SGD";

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string scriptPath = Path.Combine(baseDirectory, "Script", "train_yolov5_obb.py");
            string trainingOutputDir = Path.Combine(baseDirectory, "training_output");
            string modelSaveDir = Path.Combine(trainingOutputDir, "models");
            string trainingLogPath = Path.Combine(trainingOutputDir, "log");
            string resultsDir = Path.Combine(modelSaveDir, expName);
            string pretrainFolderPath = Path.Combine(baseDirectory, "pretrain");

            if (!File.Exists(scriptPath))
            {
                ShowError("❌ Python OBB training script not found.", $"Expected at:\n{scriptPath}", updateStatus);
                return false;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            var argsList = new List<string>
    {
        $"--data \"{datasetYamlPath}\"",
        $"--imgsz {inputWidth} {inputHeight}",
        $"--epochs \"{epochs}\"",
        $"--batch \"{batchSize}\"",
        $"--lr \"{lr}\"",
        $"--opt \"{optimizer}\"",
        $"--modelSaveDir \"{modelSaveDir}\"",
        $"--Log_dir \"{trainingLogPath}\"",
        $"--name \"{expName}\"",
        $"--base_dir \"{baseDirectory}\"",
        $"--results_dir \"{resultsDir}\"",
        $"--mode \"{trainingMode}\""
    };

            switch (trainingMode)
            {
                case "scratch":
                    string cfgPath = Path.Combine(pretrainFolderPath, "yolov5_obb.yaml");
                    if (!File.Exists(cfgPath))
                    {
                        ShowError("❌ Scratch mode requires yolov5 OBB config file.", "Training aborted due to missing config.", updateStatus);
                        return false;
                    }
                    argsList.Add($"--cfg \"{cfgPath}\"");
                    argsList.Add("--weights \"\""); // empty weights for scratch
                    updateStatus?.Invoke("🧼 Scratch mode selected. Training from config only.");
                    break;

                case "topup":
                case "benchmark":
                    if (string.IsNullOrWhiteSpace(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights not specified.", $"Training aborted for mode: {trainingMode}.", updateStatus);
                        return false;
                    }
                    if (!File.Exists(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights file not found.", $"Expected at:\n{pretrainedWeights}", updateStatus);
                        return false;
                    }
                    argsList.Add($"--weights \"{pretrainedWeights}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return false;
            }

            string args = string.Join(" ", argsList);

            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return false;
            }

            string fullCommand = $"\"{pythonPath}\" \"{scriptPath}\" {args}";
            updateStatus?.Invoke("🚀 Launching YOLOv5 OBB training script...");

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
                return true;
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch YOLOv5 OBB training script.\n{ex.Message}", "Launch Error", updateStatus);
                return false;
            }
        }
    }
}