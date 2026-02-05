using ClearEngine.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VisionAICam.Core;
using VisionAICam.Pages;
using VisionAICam.Utilities;

namespace VisionAICam.Services
{
    public class AutoLabelerService : IAutoLabelerService
    {
        private readonly ILogger _log = Logger.Instance;
        string datasetPath = "";

        public async Task<bool> RunAutoLabelingAsync(string? modelPath = null, double confidenceThreshold = 0.5, CancellationToken cancellationToken = default)
        {
            try
            {
                // Resolve model path if not provided: prefer settings registered with MasterController
                try
                {
                    var settings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
                    if (string.IsNullOrWhiteSpace(modelPath))
                        modelPath = settings?.DefaultModelPath;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"AutoLabelerService: failed to resolve settings: {ex.Message}");
                }

                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    _log.LogWarning("AutoLabelerService: no model path provided.");
                    return false;
                }

                if (cancellationToken.IsCancellationRequested) return false;

                _log.LogInfo($"AutoLabelerService: starting auto-labeling using model '{modelPath}'.");

                // Delegate to existing AutoLabeler helper (does heavy work off-UI thread internally)
                var result = await AutoLabeler.PerformAutoLabelingInferenceAsync(modelPath, confidenceThreshold).ConfigureAwait(false);

                _log.LogInfo($"AutoLabelerService: finished auto-labeling - added any: {result}");
                return result;
            }
            catch (Exception ex)
            {
                try { _log.LogError($"AutoLabelerService.RunAutoLabelingAsync failed: {ex}"); } catch { }
                return false;
            }
        }

        public bool PrepareTrainingDataset(int minImagesPerClass = 10, VisionAICam.YoloExportFormat format = VisionAICam.YoloExportFormat.YoloV8)
        {
            try
            {
                _log.LogInfo("AutoLabelerService: preparing training dataset...");
                var ok = AutoLabeler.PrepareTrainingDataset(minImagesPerClass, format);
                _log.LogInfo($"AutoLabelerService: PrepareTrainingDataset returned {ok}");
                return ok;
            }
            catch (Exception ex)
            {
                try { _log.LogError($"AutoLabelerService.PrepareTrainingDataset failed: {ex}"); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Async wrapper used by the UI to launch training without blocking the UI thread.
        /// The caller should export (or provide) a training dataset folder and pass it as datasetPath.
        /// An optional updateStatus callback runs on the caller's context (dispatch via the caller if needed).
        /// </summary>
        public Task<bool> LaunchYOLOv8TrainingAsync(string datasetPath, Action<string>? updateStatus = null)
        {
            if (string.IsNullOrWhiteSpace(datasetPath))
                return Task.FromResult(false);

            // set internal dataset root used by the synchronous launcher
            this.datasetPath = datasetPath;

            // run on thread-pool to avoid UI blocking
            return Task.Run(() =>
            {
                try
                {
                    var datasetOptions = new List<TrainingOption>();
                    var modelOptions = new List<TrainingOption>();
                    var trainingOptions = new List<TrainingOption>();
                    return LaunchYOLOv8OBBTraining(datasetOptions, modelOptions, trainingOptions, updateStatus);
                }
                catch (Exception ex)
                {
                    try { _log.LogError($"LaunchYOLOv8TrainingAsync failed: {ex}"); } catch { }
                    updateStatus?.Invoke($"Failed to start training: {ex.Message}");
                    return false;
                }
            });
        }

        private void FixDataYamlPaths(string yamlPath, string datasetRoot)
        {
            var lines = File.ReadAllLines(yamlPath).ToList();

            // Local helper: try candidates (with and without "images" subfolder). Return first existing path or default fallback.
            string ResolveImagePath(params string[] folderCandidates)
            {
                foreach (var candidate in folderCandidates)
                {
                    var pWithImages = Path.Combine(datasetRoot, candidate, "images");
                    if (Directory.Exists(pWithImages))
                        return pWithImages;

                    var pDirect = Path.Combine(datasetRoot, candidate);
                    if (Directory.Exists(pDirect))
                        return pDirect;
                }

                // fallback to first candidate + images (conventional)
                return Path.Combine(datasetRoot, folderCandidates.First(), "images");
            }

            var trainPath = ResolveImagePath("train");
            // accept either "val" or "valid" as exporters differ
            var valPath = ResolveImagePath("val", "valid");
            var testPath = ResolveImagePath("test");

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("train:"))
                    lines[i] = $"train: {trainPath.Replace("\\", "/")}";
                else if (trimmed.StartsWith("val:"))
                    lines[i] = $"val: {valPath.Replace("\\", "/")}";
                else if (trimmed.StartsWith("test:"))
                    lines[i] = $"test: {testPath.Replace("\\", "/")}";
            }

            File.WriteAllLines(yamlPath, lines);

            _log.LogInfo($"Fixed data.yaml paths: train={trainPath}, val={valPath}, test={testPath}");
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

        private static void ShowError(string messageBoxText, string statusText, Action<string> updateStatus)
        {
            // Keep a minimal UI-aware helper for now — caller may supply a UI-safe updateStatus callback.
            Application.Current?.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(messageBoxText, "Training Error", MessageBoxButton.OK, MessageBoxImage.Error);
                updateStatus?.Invoke(statusText);
            });
        }

        private static string ResolvePythonPath()
        {
            string pythonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Script", "NewEnv", "Python313", "python.exe");

            ValidatePythonPath(pythonPath);
            return pythonPath;
        }

        private static void ValidatePythonPath(string pythonPath)
        {
            if (!File.Exists(pythonPath))
                throw new FileNotFoundException($"Python executable not found at {pythonPath}");
        }

        public bool LaunchYOLOv8Training(
            List<TrainingOption> datasetOptions,
            List<TrainingOption> modelOptions,
            List<TrainingOption> trainingOptions,
            Action<string> updateStatus = null)
        {
            updateStatus?.Invoke("🔍 Validating training configuration...");

            string datasetRoot = datasetPath;
            if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
            {
                _log.LogError("Dataset path is invalid or missing.");
                updateStatus?.Invoke("Training aborted due to invalid dataset path.");
                return false;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                _log.LogError("data.yaml not found in dataset folder.");
                updateStatus?.Invoke("Training aborted due to missing data.yaml.");
                return false;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            // Use proper interpolation for experiment name
            string expName = $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputWidth = "640";
            string inputHeight = "480";
            string backbone = "yolov8n";
            string pretrainedWeights = "Pretrained Weights";
            string trainingMode = "scratch";

            string epochs = "50";
            string batchSize = "16";
            string lr = "0.001";
            string optimizer = "Adam";
            string scheduler = "StepLR";

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string scriptPath = Path.Combine(baseDirectory, "Script", "train_yolov8n_obb.py");
            string trainingOutputDir = Path.Combine(baseDirectory, "training_output");
            string modelSaveDir = Path.Combine(trainingOutputDir, "models");//correct
            string trainingLogPath = Path.Combine(trainingOutputDir, "log");
            string resultsDir = Path.Combine(modelSaveDir, expName);
            string pretrainFolderPath = Path.Combine(baseDirectory, "pretrain");//wrong

            if (!File.Exists(scriptPath))
            {
                _log.LogError($"Python training script not found. Expected at: {scriptPath}");
                updateStatus?.Invoke("Training aborted: script not found.");
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
                    string modelScale = "n";
                    argsList.Add($"--cfg \"{cfgPath}\"");
                    argsList.Add($"--model \"{modelScale}\"");
                    updateStatus?.Invoke($"🧼 Scratch mode selected. Using scale: {modelScale}");
                    break;

                case "topup":
                case "benchmark":
                    if (string.IsNullOrWhiteSpace(pretrainedWeights))
                    {
                        _log.LogError("Pretrained weights not specified.");
                        updateStatus?.Invoke($"Training aborted for mode: {trainingMode} (weights missing).");
                        return false;
                    }

                    string weightsPath = pretrainedWeights.EndsWith(".pt") ? pretrainedWeights : pretrainedWeights + ".pt";
                    if (!File.Exists(weightsPath))
                    {
                        _log.LogError($"Pretrained weights file not found: {weightsPath}");
                        updateStatus?.Invoke($"Training aborted: pretrained weights not found ({weightsPath}).");
                        return false;
                    }

                    argsList.Add($"--weights \"{weightsPath}\"");
                    updateStatus?.Invoke($"🔁 {trainingMode.ToUpper()} mode selected. Using pretrained weights...");
                    break;

                default:
                    _log.LogError($"Unknown training mode: {trainingMode}");
                    updateStatus?.Invoke("Training aborted due to invalid mode.");
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

        internal bool LaunchYOLOv8OBBTraining(
          List<TrainingOption> datasetOptions,
          List<TrainingOption> modelOptions,
          List<TrainingOption> trainingOptions,
          Action<string> updateStatus = null)
        {
            updateStatus?.Invoke("🔍 Validating training configuration...");

            string datasetRoot = datasetPath;
            if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
            {
                _log.LogError("Dataset path is invalid or missing.");
                updateStatus?.Invoke("Training aborted due to invalid dataset path.");
                return false;
            }

            string datasetYamlPath = Path.Combine(datasetRoot, "data.yaml");
            if (!File.Exists(datasetYamlPath))
            {
                _log.LogError("data.yaml not found in dataset folder.");
                updateStatus?.Invoke("Training aborted due to missing data.yaml.");
                return false;
            }

            FixDataYamlPaths(datasetYamlPath, datasetRoot);

            string expName = $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string inputWidth = "640";
            string inputHeight = "480";

            string backbone = "yolov8n-obb";
            string pretrainedWeights = "yolov8n";
            if (string.IsNullOrWhiteSpace(pretrainedWeights))
            {
                ShowError("❌ Pretrained weights not specified.", "Training aborted due to missing weights.", updateStatus);
                return false;
            }

            pretrainedWeights += "-obb.pt";

            string epochs = "50";
            string batchSize = "16";
            string lr = "0.001";
            string optimizer = "Adam";
            string taskType = "obb"; // ✅ NEW: task type

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

    }
}