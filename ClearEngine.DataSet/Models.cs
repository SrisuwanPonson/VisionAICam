using System.Collections.Generic;
using System.Drawing;

namespace ClearEngine.DataSet
{
    public enum AnnotationType
    {
        Rectangle,
        Polygon,
        FreePen,
        RotatedBox
    }

    public enum YoloExportFormat
    {
        YoloV5,
        YoloV8,
        YoloV5_OBB,
        YoloV8_OBB,
        YoloV8_SEG
    }

    public class AnnotationDto
    {
        public string ImageName { get; set; } = "";
        public string Label { get; set; } = "";
        public AnnotationType AnnotationType { get; set; }
        public List<PointF> Points { get; set; } = new();
        public List<double>? RawValues { get; set; }
    }

    public class AnnotationProjectDto
    {
        public string ProjectName { get; set; } = "";
        public List<string> ImagePaths { get; set; } = new();
        public List<string> ClassLabels { get; set; } = new();
        public List<AnnotationDto> Annotations { get; set; } = new();
        public int SelectedImageIndex { get; set; }
    }
}