using ClearEngine.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        // Auto-label model folder (used by DataSetPage/autolabel flows)
        private static readonly string AutoLabelModelDir = @"C:\ClearEngine\VisionAICam\Model\AutoLabel";
        // Production model folder (used by ModelPage / production training)
        private static readonly string ProductionModelDir = @"C:\ClearEngine\VisionAICam\Model\Production";
        // External script root (you moved script files here)
        private static readonly string ExternalScriptRoot = @"C:\ClearEngine\VisionAICam\Script";

        public async Task<bool> RunAutoLabelingAsync(string? modelPath = null, double confidenceThreshold = 0.5, System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
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
        /// Caller must provide datasetPath (folder with data.yaml). Set production=true to place outputs under ProductionModelDir.
        /// </summary>
        public Task<bool> LaunchYOLOv8TrainingAsync(string datasetPath, Action<string>? updateStatus = null, bool production = false)
        {
            if (string.IsNullOrWhiteSpace(datasetPath))
                return Task.FromResult(false);

            this.datasetPath = datasetPath;

            return Task.Run(() =>
            {
                try
                {
                    // OBB-specific training is default
                    return LaunchYOLOv8OBBTraining(null!, null!, null!, updateStatus, production);
                }
                catch (Exception ex)
                {
                    try { _log.LogError($"LaunchYOLOv8TrainingAsync failed: {ex}"); } catch { }
                    updateStatus?.Invoke($"Failed to start training: {ex.Message}");
                    return false;
                }
            });
        }

        #region Helpers

        private void FixDataYamlPaths(string yamlPath, string datasetRoot)
        {
            var lines = File.ReadAllLines(yamlPath).ToList();

            bool IsImageFile(string f)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";
            }

            string ResolveImagePath(params string[] folderCandidates)
            {
                var topImages = Path.Combine(datasetRoot, "images");
                if (Directory.Exists(topImages)) return topImages;

                try
                {
                    if (Directory.EnumerateFiles(datasetRoot).Any(f => IsImageFile(f))) return datasetRoot;
                }
                catch { }

                foreach (var candidate in folderCandidates)
                {
                    var pWithImages = Path.Combine(datasetRoot, candidate, "images");
                    if (Directory.Exists(pWithImages)) return pWithImages;

                    var pDirect = Path.Combine(datasetRoot, candidate);
                    if (Directory.Exists(pDirect)) return pDirect;
                }

                return Path.Combine(datasetRoot, folderCandidates.First(), "images");
            }

            var trainPath = ResolveImagePath("train");
            var valPath = ResolveImagePath("val", "valid");
            var testPath = ResolveImagePath("test");

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("train:")) lines[i] = $"train: {trainPath.Replace("\\", "/")}";
                else if (trimmed.StartsWith("val:")) lines[i] = $"val: {valPath.Replace("\\", "/")}";
                else if (trimmed.StartsWith("test:")) lines[i] = $"test: {testPath.Replace("\\", "/")}";
            }

            File.WriteAllLines(yamlPath, lines);
            _log.LogInfo($"Fixed data.yaml paths: train={trainPath}, val={valPath}, test={testPath}");
        }

        private static void EnsureDirectory(string path, string label)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }

        private static void ShowError(string messageBoxText, string statusText, Action<string>? updateStatus)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(messageBoxText, "Training Error", MessageBoxButton.OK, MessageBoxImage.Error);
                updateStatus?.Invoke(statusText);
            });
        }

        private static string ResolvePythonPath()
        {
            // prefer python under ExternalScriptRoot if present
            try
            {
                var cand = Path.Combine(ExternalScriptRoot, "NewEnv", "Python313", "python.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { }

            var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Script", "NewEnv", "Python313", "python.exe");
            ValidatePythonPath(fallback);
            return fallback;
        }

        private static void ValidatePythonPath(string pythonPath)
        {
            if (!File.Exists(pythonPath)) throw new FileNotFoundException($"Python executable not found at {pythonPath}");
        }

        #endregion

        #region Unified training launcher

        /// <summary>
        /// Unified training launcher.
        /// Default epochs = 20. Prefer scripts in ExternalScriptRoot; falls back to app Script folder.
        /// </summary>
        private bool LaunchTraining(
            string scriptName,
            string datasetYamlPath,
            string? pretrainedWeights,
            string taskType,
            string trainingMode,
            Action<string>? updateStatus,
            bool production,
            string inputWidth = "640",
            string inputHeight = "480",
            string epochs = "20",
            string batchSize = "16",
            string lr = "0.001",
            string optimizer = "Adam")
        {
            updateStatus?.Invoke("🔍 Validating training configuration...");

            if (string.IsNullOrWhiteSpace(datasetYamlPath) || !File.Exists(datasetYamlPath))
            {
                _log.LogError("Invalid or missing dataset YAML.");
                updateStatus?.Invoke("Training aborted: invalid dataset.");
                return false;
            }

            string expName = $"exp_{DateTime.Now:yyyyMMdd_HHmmss}";
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            // prefer external script location
            string scriptPath = Path.Combine(ExternalScriptRoot, scriptName);
            if (!File.Exists(scriptPath))
                scriptPath = Path.Combine(baseDirectory, "Script", scriptName);

            // Diagnostics: report the script path candidates
            updateStatus?.Invoke($"Looking for script: {scriptName}");
            updateStatus?.Invoke($" -> candidate external: {Path.Combine(ExternalScriptRoot, scriptName)}");
            updateStatus?.Invoke($" -> candidate local app: {Path.Combine(baseDirectory, "Script", scriptName)}");

            if (!File.Exists(scriptPath))
            {
                ShowError($"Python training script not found.\nExpected at:\n{scriptPath}", "Training aborted", updateStatus);
                return false;
            }

            string trainingOutputDir = production ? ProductionModelDir : AutoLabelModelDir;
            string modelSaveDir = Path.Combine(trainingOutputDir, "weights");
            string trainingLogPath = Path.Combine(trainingOutputDir, "log");
            string resultsDir = Path.Combine(trainingOutputDir, expName);

            EnsureDirectory(trainingOutputDir, "TrainingOutput");
            EnsureDirectory(modelSaveDir, "Weights");
            EnsureDirectory(trainingLogPath, "Log");
            EnsureDirectory(resultsDir, "Results");

            // Compose args accepted by your script.
            var argsList = new List<string>
            {
                $"--data \"{datasetYamlPath}\"",
                $"--imgsz {inputWidth} {inputHeight}",
                $"--epochs \"{epochs}\"",
                $"--batch \"{batchSize}\"",
                $"--lr \"{lr}\"",
                $"--opt \"{optimizer}\"",
                $"--name \"{expName}\"",
                $"--results_dir \"{resultsDir}\"",
                $"--task \"{taskType}\""
            };

            if (!string.IsNullOrWhiteSpace(pretrainedWeights)) argsList.Add($"--weights \"{pretrainedWeights}\"");

            // custom-script compatibility
            argsList.Add($"--modelSaveDir \"{modelSaveDir}\"");
            argsList.Add($"--Log_dir \"{trainingLogPath}\"");
            argsList.Add($"--base_dir \"{baseDirectory}\"");

            if (!string.IsNullOrWhiteSpace(trainingMode)) argsList.Add($"--mode \"{trainingMode}\"");

            string args = string.Join(" ", argsList);

            // Resolve python early (so we can use it for direct launch or as part of a cmd fallback)
            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                // Provide actionable diagnostic information
                updateStatus?.Invoke($"Python not found: {ex.Message}");
                _log.LogError($"LaunchTraining: ResolvePythonPath failed: {ex.Message}");
                return false;
            }

            // Diagnostics: confirm key paths exist and report to UI/log
            updateStatus?.Invoke($"Using python: {pythonPath}");
            updateStatus?.Invoke($"Using script: {scriptPath}");
            updateStatus?.Invoke($"Working directory: {Path.GetDirectoryName(scriptPath) ?? baseDirectory}");

            string fullCommand = $"\"{pythonPath}\" \"{scriptPath}\" {args}";
            updateStatus?.Invoke($"🚀 Launching training script...\n{fullCommand}");

            // Try to start Python directly first.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"\"{scriptPath}\" {args}",
                    UseShellExecute = true,            // required to redirect streams
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? baseDirectory
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.EnableRaisingEvents = true;

                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            updateStatus?.Invoke(e.Data);
                    };
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            updateStatus?.Invoke($"ERR: {e.Data}");
                    };

                    // Begin async read so process doesn't block
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    _log.LogInfo($"Launched python process (pid={proc.Id}) for script {scriptPath}");
                    return true;
                }
                else
                {
                    updateStatus?.Invoke("Direct python start returned null Process instance.");
                    _log.LogWarning("Direct python start returned null Process");
                }
            }
            catch (Exception exDirect)
            {
                updateStatus?.Invoke($"Direct python start failed: {exDirect.Message}");
                _log.LogWarning($"Direct python start failed: {exDirect}");
                // fallthrough to cmd fallback
            }

            // Fallback: launch via system cmd.exe using COMSPEC (robust across Windows configurations)
            try
            {
                string comspec = Environment.GetEnvironmentVariable("COMSPEC") ??
                                 Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

                updateStatus?.Invoke($"Fallback: launching via cmd.exe at: {comspec}");

                if (!File.Exists(comspec))
                {
                    ShowError($"cmd.exe not found at expected location: {comspec}\nCannot launch training.", "Training aborted", updateStatus);
                    _log.LogError($"cmd.exe missing at {comspec}");
                    return false;
                }

                // CMD will live-stream status from cmd_status.txt
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"{fullCommand}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                Process.Start(startInfo);
                _log.LogInfo($"Launched training using cmd.exe -> {comspec}");
                return true;
            }
            catch (Exception exCmd)
            {
                ShowError($"Failed to launch training script.\n{exCmd.Message}", "Launch Error", updateStatus);
                _log.LogError($"LaunchTraining fallback failed: {exCmd}");
                return false;
            }

        }

        #endregion

        #region Legacy adapters

        public bool LaunchYOLOv8Training(
            List<TrainingOption> datasetOptions,
            List<TrainingOption> modelOptions,
            List<TrainingOption> trainingOptions,
            Action<string> updateStatus = null,
            bool production = false)
        {
            string datasetYamlPath = Path.Combine(datasetPath, "data.yaml");
            return LaunchTraining(
                scriptName: "train_yolov8n_obb.py",
                datasetYamlPath: datasetYamlPath,
                pretrainedWeights: string.Empty,
                taskType: "obb",
                trainingMode: "scratch",
                updateStatus: updateStatus,
                production: production);
        }

        internal bool LaunchYOLOv8OBBTraining(
          List<TrainingOption> datasetOptions,
          List<TrainingOption> modelOptions,
          List<TrainingOption> trainingOptions,
          Action<string> updateStatus = null,
          bool production = false)
        {
            string datasetYamlPath = Path.Combine(datasetPath, "data.yaml");
            return LaunchTraining(
                scriptName: "train_yolov8_obb.py",
                datasetYamlPath: datasetYamlPath,
                pretrainedWeights: "yolov8n-obb.pt",
                taskType: "obb",
                trainingMode: "topup",
                updateStatus: updateStatus,
                production: production);
        }

        #endregion
    }
}