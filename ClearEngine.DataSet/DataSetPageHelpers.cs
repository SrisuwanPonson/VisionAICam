using System.Drawing;
using System.Linq;
using ClearEngine.DataSet;

namespace VisionAICam.Pages
{
    // Add these helper methods into your WPF project (DataSetPage or a partial class)
    internal static class DataSetPageHelpers
    {
        public static AnnotationDto ToDto(this AnnotationRecord r)
        {
            return new AnnotationDto
            {
                ImageName = r.ImageName,
                Label = r.Label,
                AnnotationType = (ClearEngine.DataSet.AnnotationType)Enum.Parse(typeof(ClearEngine.DataSet.AnnotationType), r.AnnotationType.ToString()),
                Points = r.Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToList(),
                RawValues = r.RawValues != null ? r.RawValues.ToList() : null
            };
        }

        public static AnnotationProjectDto ToDto(this AnnotationProject p, System.Collections.Generic.List<AnnotationRecord> localAnnotations)
        {
            return new AnnotationProjectDto
            {
                ProjectName = p.ProjectName,
                ImagePaths = p.ImagePaths?.ToList() ?? new System.Collections.Generic.List<string>(),
                ClassLabels = p.ClassLabels?.ToList() ?? new System.Collections.Generic.List<string>(),
                SelectedImageIndex = p.SelectedImageIndex,
                Annotations = localAnnotations.Select(a => a.ToDto()).ToList()
            };
        }
    }
}