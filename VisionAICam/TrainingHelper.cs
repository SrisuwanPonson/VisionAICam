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
        internal static void LaunchYOLOv8Training(
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
                return;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            // Extract training parameters
            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputSize = GetOptionValue(modelOptions, "Input Size") ?? "640";
            string backbone = GetOptionValue(modelOptions, "Backbone") ?? "yolov8n";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "Adam";
            string scheduler = GetOptionValue(trainingOptions, "Scheduler") ?? "StepLR";

            // Resolve directories
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
                return;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            // Build argument list
            var argsList = new List<string>
            {
                $"--data \"{datasetYamlPath}\"",
                $"--imgsz \"{inputSize}\"",
                $"--backbone \"{backbone}\"",
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
                $"--results_dir \"{resultsDir}\"",
                $"--mode \"{trainingMode}\""
            };

            // 🧠 Handle training mode
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
                    if (string.IsNullOrWhiteSpace(pretrainedWeights) || !File.Exists(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights file is missing or invalid.", $"Training aborted for mode: {trainingMode}.", updateStatus);
                        return;
                    }
                    argsList.Add($"--weights \"{pretrainedWeights}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return;
            }

            string args = string.Join(" ", argsList);

            // Resolve Python path
            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return;
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
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch training script.\n{ex.Message}", "Launch Error", updateStatus);
            }
        }
        internal static void LaunchYOLOv5Training(
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
                return;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            // Extract training parameters
            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputSize = GetOptionValue(modelOptions, "Input Size") ?? "640";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "SGD";

            // Resolve directories
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
                return;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            // Build argument list
            var argsList = new List<string>
            {
                $"--data \"{datasetYamlPath}\"",
                $"--imgsz \"{inputSize}\"",
                $"--epochs \"{epochs}\"",
                $"--batch \"{batchSize}\"",
                $"--lr \"{lr}\"",
                $"--optimizer \"{optimizer}\"",
                $"--name \"{expName}\"",
                $"--project \"{modelSaveDir}\"",
                $"--exist-ok",
                $"--results_dir \"{resultsDir}\"",
                $"--mode \"{trainingMode}\""
            };

            // 🧠 Handle training mode
            switch (trainingMode)
            {
                case "scratch":
                    string cfgPath = Path.Combine(pretrainFolderPath, "yolov5s.yaml");
                    if (!File.Exists(cfgPath))
                    {
                        ShowError("❌ Scratch mode requires yolov5 config file.", "Training aborted due to missing config.", updateStatus);
                        return;
                    }
                    argsList.Add($"--cfg \"{cfgPath}\"");
                    argsList.Add("--weights \"\""); // empty weights for scratch
                    updateStatus?.Invoke("🧼 Scratch mode selected. Training from config only.");
                    break;

                case "topup":
                case "benchmark":
                    if (string.IsNullOrWhiteSpace(pretrainedWeights) || !File.Exists(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights file is missing or invalid.", $"Training aborted for mode: {trainingMode}.", updateStatus);
                        return;
                    }
                    argsList.Add($"--weights \"{pretrainedWeights}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return;
            }

            string args = string.Join(" ", argsList);

            // Resolve Python path
            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return;
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
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch training script.\n{ex.Message}", "Launch Error", updateStatus);
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

        internal static void LaunchYOLOv8SegmentationTraining(
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
                return;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            // Extract training parameters
            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputSize = GetOptionValue(modelOptions, "Input Size") ?? "640";
            string backbone = GetOptionValue(modelOptions, "Backbone") ?? "yolov8n";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "Adam";
            string scheduler = GetOptionValue(trainingOptions, "Scheduler") ?? "StepLR";

            // Resolve directories
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
                return;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            // Build argument list
            var argsList = new List<string>
            {
                $"--data \"{datasetYamlPath}\"",
                $"--imgsz \"{inputSize}\"",
                $"--backbone \"{backbone}\"",
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
                $"--results_dir \"{resultsDir}\"",
                $"--mode \"{trainingMode}\""
            };

            // 🧠 Handle training mode
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
                        return;
                    }
                    argsList.Add($"--weights \"{pretrainedWeights}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return;
            }

            string args = string.Join(" ", argsList);

            // Resolve Python path
            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return;
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
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch segmentation training script.\n{ex.Message}", "Launch Error", updateStatus);
            }
        }

        internal static void LaunchYOLOv8OBBTraining(
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
                return;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            // Extract training parameters
            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputSize = GetOptionValue(modelOptions, "Input Size") ?? "640";
            string backbone = GetOptionValue(modelOptions, "Backbone") ?? "yolov8n-obb";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "Adam";
            string scheduler = GetOptionValue(trainingOptions, "Scheduler") ?? "StepLR";

            // Resolve directories
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string scriptPath = Path.Combine(baseDirectory, "Script", "train_yolov8_obb.py");
            string trainingOutputDir = Path.Combine(baseDirectory, "training_output");
            string modelSaveDir = Path.Combine(trainingOutputDir, "models");
            string trainingLogPath = Path.Combine(trainingOutputDir, "log");
            string resultsDir = Path.Combine(modelSaveDir, expName);
            string pretrainFolderPath = Path.Combine(baseDirectory, "pretrain");

            if (!File.Exists(scriptPath))
            {
                ShowError("❌ Python OBB training script not found.", $"Expected at:\n{scriptPath}", updateStatus);
                return;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            // Build argument list
            var argsList = new List<string>
            {
                $"--data \"{datasetYamlPath}\"",
                $"--imgsz \"{inputSize}\"",
                $"--backbone \"{backbone}\"",
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
                $"--results_dir \"{resultsDir}\"",
                $"--mode \"{trainingMode}\""
            };

            // 🧠 Handle training mode
            switch (trainingMode)
            {
                case "scratch":
                    string cfgPath = Path.Combine(pretrainFolderPath, "yolov8_obb.yaml");
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
                        return;
                    }
                    argsList.Add($"--weights \"{pretrainedWeights}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return;
            }

            string args = string.Join(" ", argsList);

            // Resolve Python path
            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return;
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
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch OBB training script.\n{ex.Message}", "Launch Error", updateStatus);
            }
        }

        internal static void LaunchYOLOv5OBBTraining(
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
                return;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                ShowError("❌ data.yaml not found in dataset folder.", "Training aborted due to missing data.yaml.", updateStatus);
                return;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            // Extract training parameters
            string expName = GetOptionValue(trainingOptions, "Experiment Name") ?? $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputSize = GetOptionValue(modelOptions, "Input Size") ?? "640";
            string pretrainedWeights = GetOptionValue(modelOptions, "Pretrained Weights");
            string trainingMode = GetOptionValue(trainingOptions, "Training Mode")?.ToLower() ?? "scratch";

            string epochs = GetOptionValue(trainingOptions, "Epochs") ?? "50";
            string batchSize = GetOptionValue(trainingOptions, "Batch Size") ?? "16";
            string lr = GetOptionValue(trainingOptions, "Learning Rate") ?? "0.001";
            string optimizer = GetOptionValue(trainingOptions, "Optimizer") ?? "SGD";

            // Resolve directories
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
                return;
            }

            EnsureDirectory(trainingOutputDir, "Training Output");
            EnsureDirectory(modelSaveDir, "Model Save");
            EnsureDirectory(trainingLogPath, "Training Log");
            EnsureDirectory(resultsDir, "Results Output");

            // Build argument list
            var argsList = new List<string>
            {
                $"--data \"{datasetYamlPath}\"",
                $"--imgsz \"{inputSize}\"",
                $"--epochs \"{epochs}\"",
                $"--batch \"{batchSize}\"",
                $"--lr \"{lr}\"",
                $"--optimizer \"{optimizer}\"",
                $"--name \"{expName}\"",
                $"--project \"{modelSaveDir}\"",
                $"--exist-ok",
                $"--results_dir \"{resultsDir}\"",
                $"--mode \"{trainingMode}\""
            };

            // 🧠 Handle training mode
            switch (trainingMode)
            {
                case "scratch":
                    string cfgPath = Path.Combine(pretrainFolderPath, "yolov5_obb.yaml");
                    if (!File.Exists(cfgPath))
                    {
                        ShowError("❌ Scratch mode requires yolov5 OBB config file.", "Training aborted due to missing config.", updateStatus);
                        return;
                    }
                    argsList.Add($"--cfg \"{cfgPath}\"");
                    argsList.Add("--weights \"\""); // empty weights for scratch
                    updateStatus?.Invoke("🧼 Scratch mode selected. Training from config only.");
                    break;

                case "topup":
                case "benchmark":
                    if (string.IsNullOrWhiteSpace(pretrainedWeights) || !File.Exists(pretrainedWeights))
                    {
                        ShowError("❌ Pretrained weights file is missing or invalid.", $"Training aborted for mode: {trainingMode}.", updateStatus);
                        return;
                    }
                    argsList.Add($"--weights \"{pretrainedWeights}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    ShowError($"❌ Unknown training mode: {trainingMode}", "Training aborted due to invalid mode.", updateStatus);
                    return;
            }

            string args = string.Join(" ", argsList);

            // Resolve Python path
            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                updateStatus?.Invoke(ex.Message);
                return;
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
            }
            catch (Exception ex)
            {
                ShowError($"❌ Failed to launch YOLOv5 OBB training script.\n{ex.Message}", "Launch Error", updateStatus);
            }
        }
    }
}