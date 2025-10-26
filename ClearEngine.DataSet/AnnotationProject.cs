using System.Collections.Generic;

namespace ClearEngine.DataSet
{
    // UI-agnostic project model for dataset logic (serializable, independent from WPF)
    public class AnnotationProject
    {
        public string ProjectName { get; set; } = string.Empty;
        public List<string> ImagePaths { get; set; } = new();
        public List<string> ClassLabels { get; set; } = new();
        public List<AnnotationRecord> Annotations { get; set; } = new();
        public int SelectedImageIndex { get; set; } = 0;
    }
}