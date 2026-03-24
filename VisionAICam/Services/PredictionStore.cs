using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiteDB;

namespace VisionAICam.Services
{
    public class PredictionEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string ClassName { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Box { get; set; } = string.Empty;
        public string Task { get; set; } = string.Empty;
        public string Source { get; set; } = "Production";
        public string? FrameId { get; set; }
    }

    public sealed class PredictionStore : IDisposable
    {
        private static readonly Lazy<PredictionStore> _lazy = new(() => new PredictionStore());
        public static PredictionStore Instance => _lazy.Value;

        private readonly LiteDatabase _db;
        private readonly ILiteCollection<PredictionEntity> _col;

        // Event raised when database contents change (subscribers should marshal to UI thread)
        public event EventHandler? DatabaseChanged;

        private PredictionStore()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VisionAICam");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "predictions.db");

            // Use connection-string overload to be explicit; LiteDB defaults are safe.
            _db = new LiteDatabase(new ConnectionString($"Filename={path};Journal=True"));

            _col = _db.GetCollection<PredictionEntity>("predictions");
            _col.EnsureIndex(x => x.Timestamp);
            _col.EnsureIndex(x => x.ClassName);
        }

        private void RaiseDatabaseChanged()
        {
            try { DatabaseChanged?.Invoke(this, EventArgs.Empty); } catch { /* swallow */ }
        }

        // Insert many (append)
        public Task InsertManyAsync(IEnumerable<PredictionEntity> items)
        {
            if (items == null) return Task.CompletedTask;
            var list = items.ToList();
            return Task.Run(() =>
            {
                try
                {
                    _col.InsertBulk(list);
                    RaiseDatabaseChanged();
                }
                catch { /* swallow to avoid camera loop exceptions */ }
            });
        }

        // Replace entire collection with latest items (delete previous, insert new).
        // Keep for compatibility.
        public Task ReplaceWithLatestAsync(IEnumerable<PredictionEntity> items)
        {
            var list = items?.ToList() ?? new List<PredictionEntity>();
            return Task.Run(() =>
            {
                try
                {
                    // Delete all previous predictions first then insert the latest
                    _col.DeleteAll();
                    if (list.Count > 0)
                        _col.InsertBulk(list);

                    RaiseDatabaseChanged();
                }
                catch
                {
                    // swallow; caller may log if needed
                }
            });
        }

        // Convenience mapping from Production DetectionResult (UI DTO) to persistence DTO
        public Task InsertDetectionResultsAsync(IEnumerable<VisionAICam.Pages.DetectionResult> results, string? frameId = null)
        {
            if (results == null) return Task.CompletedTask;
            var entities = results.Select(r => new PredictionEntity
            {
                Timestamp = DateTime.UtcNow,
                ClassName = r.ClassName ?? string.Empty,
                Confidence = r.Confidence,
                Box = r.Box ?? string.Empty,
                Task = r.Task ?? string.Empty,
                Source = "Production",
                FrameId = frameId
            }).ToList();

            return InsertManyAsync(entities);
        }

        // Convenience replace (keep only this update)
        public Task ReplaceDetectionResultsAsync(IEnumerable<VisionAICam.Pages.DetectionResult> results, string? frameId = null)
        {
            if (results == null) return Task.Run(() => { _col.DeleteAll(); RaiseDatabaseChanged(); });
            var entities = results.Select(r => new PredictionEntity
            {
                Timestamp = DateTime.UtcNow,
                ClassName = r.ClassName ?? string.Empty,
                Confidence = r.Confidence,
                Box = r.Box ?? string.Empty,
                Task = r.Task ?? string.Empty,
                Source = "Production",
                FrameId = frameId
            }).ToList();

            return ReplaceWithLatestAsync(entities);
        }

        // Update-only strategy — modify DB so only changed records are updated,
        // new ones are inserted and removed ones deleted (key = ClassName|Box|Task).
        public Task ReplaceDetectionResultsDeltaAsync(IEnumerable<VisionAICam.Pages.DetectionResult> results, string? frameId = null)
        {
            var newList = (results ?? Enumerable.Empty<VisionAICam.Pages.DetectionResult>())
                          .Select(r => new PredictionEntity
                          {
                              Timestamp = DateTime.UtcNow,
                              ClassName = r.ClassName ?? string.Empty,
                              Confidence = r.Confidence,
                              Box = r.Box ?? string.Empty,
                              Task = r.Task ?? string.Empty,
                              Source = "Production",
                              FrameId = frameId
                          })
                          .ToList();

            return Task.Run(() =>
            {
                try
                {
                    // Build keyed maps for quick diffing
                    static string KeyFor(PredictionEntity e) =>
                        $"{(e.ClassName ?? "").Trim().ToLowerInvariant()}|{(e.Box ?? "").Trim()}|{(e.Task ?? "").Trim().ToLowerInvariant()}";

                    var newMap = newList.ToDictionary(k => KeyFor(k), v => v);

                    // Load existing entries (the "last update" set)
                    var existing = _col.FindAll().ToList();
                    var existingMap = existing.ToDictionary(e => KeyFor(e), e => e);

                    // Determine deletions: existing keys not in newMap
                    var toDeleteKeys = existingMap.Keys.Except(newMap.Keys).ToList();

                    // Determine inserts: new keys not in existingMap
                    var toInsertKeys = newMap.Keys.Except(existingMap.Keys).ToList();
                    var toInsert = toInsertKeys.Select(k => newMap[k]).ToList();

                    // Determine updates: keys present in both but with different data (confidence/box/frame)
                    var toUpdate = new List<PredictionEntity>();
                    foreach (var key in existingMap.Keys.Intersect(newMap.Keys))
                    {
                        var oldE = existingMap[key];
                        var newE = newMap[key];

                        bool confidenceChanged = Math.Abs(oldE.Confidence - newE.Confidence) > 1e-6;
                        bool boxChanged = !string.Equals(oldE.Box ?? "", newE.Box ?? "", StringComparison.Ordinal);
                        bool frameChanged = (oldE.FrameId ?? "") != (newE.FrameId ?? "");

                        if (confidenceChanged || boxChanged || frameChanged)
                        {
                            // update existing record in-place (preserve Id)
                            oldE.Confidence = newE.Confidence;
                            oldE.Box = newE.Box;
                            oldE.FrameId = newE.FrameId;
                            oldE.Timestamp = DateTime.UtcNow;
                            toUpdate.Add(oldE);
                        }
                    }

                    // Perform DB operations in a simple order: deletes, updates, inserts
                    foreach (var key in toDeleteKeys)
                    {
                        try
                        {
                            var e = existingMap[key];
                            _col.Delete(e.Id);
                        }
                        catch { /* swallow individual delete errors */ }
                    }

                    foreach (var e in toUpdate)
                    {
                        try { _col.Update(e); } catch { /* swallow update errors */ }
                    }

                    if (toInsert.Count > 0)
                    {
                        try { _col.InsertBulk(toInsert); } catch { /* swallow insert errors */ }
                    }

                    // Notify listeners that DB changed
                    RaiseDatabaseChanged();
                }
                catch
                {
                    // swallow overall errors to avoid impacting camera/inference loop
                }
            });
        }

        public IEnumerable<PredictionEntity> QueryRecent(int max = 100)
            => _col.Query().OrderByDescending(x => x.Timestamp).Limit(max).ToList();

        public IEnumerable<PredictionEntity> QueryByClass(string className, DateTime? since = null)
        {
            var q = _col.Query().Where(x => x.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase));
            if (since.HasValue) q = q.Where(x => x.Timestamp >= since.Value);
            return q.ToList();
        }

        public void Dispose()
        {
            try { _db?.Dispose(); } catch { }
        }
    }
}