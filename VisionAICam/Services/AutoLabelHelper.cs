using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClearEngine.Model.Inference;
using VisionAICam.Pages;

namespace VisionAICam.Services
{
    /// <summary>
    /// Helper to run auto-labeling via the project's InferenceEngine and produce AnnotationRecord instances.
    /// Designed to be called from DataSetPage (or any UI code). Uses MasterController's registered engine if none provided.
    /// </summary>
    public class AutoLabelHelper
    {
        private readonly InferenceEngine _engine;
        private readonly string? _logDir;

        public AutoLabelHelper(InferenceEngine? engine = null, string? logDir = null)
        {
            _engine = engine ?? VisionAICam.Core.MasterController.Instance.GetInferenceEngine()
                      ?? throw new InvalidOperationException("InferenceEngine not available. Register or provide one.");
            _logDir = logDir ?? LoggerPathSafe();
        }

        /// <summary>
        /// Auto-label a single image. Returns a list of AnnotationRecord mapped from detection results.
        /// </summary>
        public async Task<List<AnnotationRecord>> LabelImageAsync(string imagePath, string? modelPath = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentNullException(nameof(imagePath));
            if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found", imagePath);

            byte[] bytes = await Task.Run(() => File.ReadAllBytes(imagePath), ct).ConfigureAwait(false);

            // Call inference on background thread to avoid blocking UI
            var detections = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                // engine.Detect signature varies; call the common form used elsewhere
                var res = _engine.Detect(bytes, modelPath ?? _engine.modelPath, _logDir);
                return res;
            }, ct).ConfigureAwait(false);

            return MapDetectionsToAnnotations(detections, Path.GetFileName(imagePath));
        }

        /// <summary>
        /// Batch label images in a folder (non-recursive). Returns map of image filename -> annotations.
        /// </summary>
        public async Task<Dictionary<string, List<AnnotationRecord>>> LabelFolderAsync(string folderPath, string? modelPath = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentNullException(nameof(folderPath));
            if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException(folderPath);

            var images = Directory.GetFiles(folderPath)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var result = new Dictionary<string, List<AnnotationRecord>>(StringComparer.OrdinalIgnoreCase);

            foreach (var img in images)
            {
                ct.ThrowIfCancellationRequested();
                var ann = await LabelImageAsync(img, modelPath, ct).ConfigureAwait(false);
                result[Path.GetFileName(img)] = ann;
            }

            return result;
        }

        /// <summary>
        /// Adds auto-label annotations for a single image into the given project (in-memory).
        /// Returns number of annotations added.
        /// </summary>
        public async Task<int> ApplyLabelsToProjectAsync(AnnotationProject project, string imagePath, string? modelPath = null, CancellationToken ct = default)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var anns = await LabelImageAsync(imagePath, modelPath, ct).ConfigureAwait(false);
            if (project.Annotations == null) project.Annotations = new List<AnnotationRecord>();
            project.Annotations.AddRange(anns);
            return anns.Count;
        }

        // Map arbitrary detection objects returned by the inference engine into AnnotationRecord.
        private List<AnnotationRecord> MapDetectionsToAnnotations(object detectionsObj, string imageFileName)
        {
            var list = new List<AnnotationRecord>();
            if (detectionsObj == null) return list;

            // If it's already an IEnumerable, iterate; otherwise treat as single item
            IEnumerable<object> items;
            if (detectionsObj is System.Collections.IEnumerable en)
                items = en.Cast<object>();
            else
                items = new[] { detectionsObj };

            foreach (var item in items)
            {
                if (item == null) continue;

                string label = TryGetStringProp(item, "ClassName") ??
                               TryGetStringProp(item, "Label") ??
                               TryGetStringProp(item, "Class") ??
                               "unknown";

                // Try to get RawValues (OBB) or Points (polygon) or bbox
                var rawValues = TryGetDoubleEnumerable(item, "RawValues")?.ToList();
                var pts = TryGetPointEnumerable(item, "Points")?.ToList();
                var bbox = TryGetDoubleEnumerable(item, "BoundingBox")?.ToList(); // optional

                var rec = new AnnotationRecord
                {
                    ImageName = imageFileName,
                    Label = label
                };

                if (rawValues != null && rawValues.Count >= 8)
                {
                    rec.AnnotationType = AnnotationType.RotatedBox;
                    rec.RawValues = rawValues;
                    // keep Points minimal representation (four corner points) if desired
                    rec.Points = new List<System.Windows.Point>
                        {
                            new System.Windows.Point(rawValues[0], rawValues[1]),
                            new System.Windows.Point(rawValues[2], rawValues[3]),
                            new System.Windows.Point(rawValues[4], rawValues[5]),
                            new System.Windows.Point(rawValues[6], rawValues[7])
                        };
                }
                else if (pts != null && pts.Count > 2)
                {
                    rec.AnnotationType = AnnotationType.Polygon;
                    rec.Points = pts.Select(p => new System.Windows.Point(p.X, p.Y)).ToList();
                }
                else if (bbox != null && bbox.Count >= 4)
                {
                    // assume [x,y,w,h] or [x1,y1,x2,y2]
                    if (bbox.Count == 4)
                    {
                        // treat as rectangle (x,y,w,h)
                        double x = bbox[0], y = bbox[1], w = bbox[2], h = bbox[3];
                        rec.AnnotationType = AnnotationType.Rectangle;
                        rec.Points = new List<System.Windows.Point> { new System.Windows.Point(x, y), new System.Windows.Point(x + w, y + h) };
                    }
                    else if (bbox.Count >= 8)
                    {
                        // 4 corners
                        rec.AnnotationType = AnnotationType.RotatedBox;
                        rec.RawValues = bbox.Take(8).ToList();
                        rec.Points = new List<System.Windows.Point>
                        {
                            new System.Windows.Point(bbox[0], bbox[1]),
                            new System.Windows.Point(bbox[2], bbox[3]),
                            new System.Windows.Point(bbox[4], bbox[5]),
                            new System.Windows.Point(bbox[6], bbox[7])
                        };
                    }
                }
                else
                {
                    // fallback: simple rectangle-less free label
                    rec.AnnotationType = AnnotationType.Rectangle;
                    rec.Points = new List<System.Windows.Point>(); // unknown geometry
                }

                list.Add(rec);
            }

            return list;
        }

        // reflection helpers - tolerant to different detection DTO shapes
        private static string? TryGetStringProp(object obj, string propName)
        {
            var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null) return null;
            var v = p.GetValue(obj);
            return v?.ToString();
        }

        private static IEnumerable<double>? TryGetDoubleEnumerable(object obj, string propName)
        {
            var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null) return null;
            var v = p.GetValue(obj);
            if (v == null) return null;
            if (v is IEnumerable<double> d) return d;
            if (v is IEnumerable<object> o) return o.Select(o2 => ConvertToDoubleSafe(o2)).Where(dv => dv.HasValue).Select(dv => dv!.Value);
            return null;
        }

        private static IEnumerable<(double X, double Y)>? TryGetPointEnumerable(object obj, string propName)
        {
            var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p == null) return null;
            var v = p.GetValue(obj);
            if (v == null) return null;

            var items = new List<(double X, double Y)>();
            foreach (var item in (v as System.Collections.IEnumerable) ?? Enumerable.Empty<object>())
            {
                if (item == null) continue;
                // if item has X/Y or x/y or has two numbers
                var tx = item.GetType().GetProperty("X") ?? item.GetType().GetProperty("x");
                var ty = item.GetType().GetProperty("Y") ?? item.GetType().GetProperty("y");
                if (tx != null && ty != null)
                {
                    var xv = tx.GetValue(item); var yv = ty.GetValue(item);
                    var xd = ConvertToDoubleSafe(xv); var yd = ConvertToDoubleSafe(yv);
                    if (xd.HasValue && yd.HasValue) items.Add((xd.Value, yd.Value));
                    continue;
                }

                // otherwise, try if item is double[] or two-element array
                if (item is IEnumerable<object> seq)
                {
                    var arr = seq.Select(o => ConvertToDoubleSafe(o)).Where(n => n.HasValue).Select(n => n!.Value).ToArray();
                    if (arr.Length >= 2) items.Add((arr[0], arr[1]));
                }
            }

            return items.Count > 0 ? items : null;
        }

        private static double? ConvertToDoubleSafe(object? o)
        {
            if (o == null) return null;
            if (o is double d) return d;
            if (o is float f) return (double)f;
            if (o is int i) return i;
            if (o is long l) return l;
            if (double.TryParse(o.ToString(), out var parsed)) return parsed;
            return null;
        }

        private static string? LoggerPathSafe()
        {
            try
            {
                return global::ClearEngine.Logging.Logger.Instance.GetLogDirectory();
            }
            catch
            {
                return null;
            }
        }
    }
}